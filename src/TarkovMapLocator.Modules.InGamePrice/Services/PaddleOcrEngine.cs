using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using CvRect = OpenCvSharp.Rect;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;

namespace TarkovMapLocator.Modules.InGamePrice.Services;

/// <summary>
/// PP-OCRv4 text detection + recognition over ONNX Runtime.  The detector
/// (DBNet) returns the bounds of every text line in a frame, which replaces the
/// scale-sensitive "kg" template anchor entirely; the CTC recognizer reads the
/// detected lines directly. One engine belongs to one recognition session. Disposing the session also
/// disposes both ONNX sessions so stopping OCR can return the model memory to
/// the operating system instead of retaining it for the process lifetime.
/// </summary>
public sealed class PaddleOcrEngine : IDisposable
{
    private const int DetLimitSideLength = 960;
    private const float DetBinaryThreshold = 0.3f;
    private const float DetBoxScoreThreshold = 0.5f;
    private const double DetUnclipRatio = 1.6;
    private const int RecInputHeight = 48;
    private const int RecMaxInputWidth = 1600;

    private readonly InferenceSession _detSession;
    private readonly InferenceSession _recSession;
    private readonly string _detInputName;
    private readonly string _recInputName;
    private readonly string[] _keys;
    private bool _disposed;

    public PaddleOcrEngine(string modelDirectory)
    {
        var detPath = Path.Combine(modelDirectory, "ch_PP-OCRv4_det_infer.onnx");
        var recPath = Path.Combine(modelDirectory, "ch_PP-OCRv4_rec_infer.onnx");
        var keysPath = Path.Combine(modelDirectory, "ppocr_keys_v1.txt");
        if (!File.Exists(detPath)) throw new FileNotFoundException("未找到 PP-OCR 文本检测模型。", detPath);
        if (!File.Exists(recPath)) throw new FileNotFoundException("未找到 PP-OCR 文本识别模型。", recPath);
        if (!File.Exists(keysPath)) throw new FileNotFoundException("未找到 PP-OCR 字符字典。", keysPath);

        _keys = File.ReadAllLines(keysPath);
        if (_keys.Length < 1000)
            throw new InvalidDataException($"PP-OCR 字符字典行数异常（{_keys.Length} 行）。");

        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4)
        };
        InferenceSession? detSession = null;
        try
        {
            detSession = new InferenceSession(detPath, options);
            _recSession = new InferenceSession(recPath, options);
            _detSession = detSession;
        }
        catch
        {
            detSession?.Dispose();
            throw;
        }
        _detInputName = First(_detSession.InputMetadata.Keys);
        _recInputName = First(_recSession.InputMetadata.Keys);
    }

    public readonly record struct RecognizedLine(string Text, double Confidence);

    /// <summary>
    /// Runs DBNet over the frame and returns axis-aligned text-line bounds in
    /// the frame's own pixel space.  Game UI text is horizontal, so the rotated
    /// candidate boxes are flattened to their bounding rectangles.
    /// </summary>
    public IReadOnlyList<CvRect> DetectTextBoxes(Mat bgr, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var sourceWidth = bgr.Cols;
        var sourceHeight = bgr.Rows;
        var maxSide = Math.Max(sourceWidth, sourceHeight);
        var ratio = maxSide > DetLimitSideLength ? (double)DetLimitSideLength / maxSide : 1d;
        var resizedWidth = RoundToMultipleOf32(sourceWidth * ratio);
        var resizedHeight = RoundToMultipleOf32(sourceHeight * ratio);

        using var resized = new Mat();
        Cv2.Resize(bgr, resized, new Size(resizedWidth, resizedHeight));
        var tensor = ToNormalizedChwTensor(resized, ImageNetNormalization: true);

        cancellationToken.ThrowIfCancellationRequested();
        using var results = _detSession.Run([NamedOnnxValue.CreateFromTensor(_detInputName, tensor)]);
        var probability = (DenseTensor<float>)results.First().AsTensor<float>();
        var probabilityBuffer = probability.Buffer.ToArray();

        using var probabilityMat = new Mat(resizedHeight, resizedWidth, MatType.CV_32FC1);
        Marshal.Copy(probabilityBuffer, 0, probabilityMat.Data, resizedHeight * resizedWidth);
        using var binary = new Mat();
        Cv2.Threshold(probabilityMat, binary, DetBinaryThreshold, 255, ThresholdTypes.Binary);
        using var binary8 = new Mat();
        binary.ConvertTo(binary8, MatType.CV_8UC1);

        Cv2.FindContours(binary8, out var contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);
        var scaleX = (double)sourceWidth / resizedWidth;
        var scaleY = (double)sourceHeight / resizedHeight;
        var boxes = new List<CvRect>();
        foreach (var contour in contours)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rotated = Cv2.MinAreaRect(contour);
            var shortSide = Math.Min(rotated.Size.Width, rotated.Size.Height);
            if (shortSide < 3) continue;
            if (BoxScore(probabilityMat, contour) < DetBoxScoreThreshold) continue;

            // The DB probability map hugs the glyph cores; the standard unclip
            // step grows each box back to the visual extent of the text line.
            var area = (double)rotated.Size.Width * rotated.Size.Height;
            var perimeter = 2d * (rotated.Size.Width + rotated.Size.Height);
            var distance = perimeter > 0 ? area * DetUnclipRatio / perimeter : 0;
            var unclipped = new RotatedRect(
                rotated.Center,
                new Size2f(rotated.Size.Width + 2 * (float)distance, rotated.Size.Height + 2 * (float)distance),
                rotated.Angle);

            var bounds = Cv2.BoundingRect(unclipped.Points().Select(point => new Point((int)point.X, (int)point.Y)));
            var x = (int)Math.Round(bounds.X * scaleX);
            var y = (int)Math.Round(bounds.Y * scaleY);
            var width = (int)Math.Round(bounds.Width * scaleX);
            var height = (int)Math.Round(bounds.Height * scaleY);
            x = Math.Clamp(x, 0, sourceWidth - 1);
            y = Math.Clamp(y, 0, sourceHeight - 1);
            width = Math.Clamp(width, 1, sourceWidth - x);
            height = Math.Clamp(height, 1, sourceHeight - y);
            if (width < 8 || height < 6) continue;
            boxes.Add(new CvRect(x, y, width, height));
        }

        return boxes;
    }

    /// <summary>
    /// Recognizes a single horizontal text line cropped from the frame.  The
    /// detector's boxes hug the glyph cores, so the crop is padded a little
    /// (30% of the line height on each side, 12% vertically) to keep the first
    /// and last characters' edge strokes intact.
    /// </summary>
    public RecognizedLine RecognizeLine(Mat bgr, CvRect box, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var padX = (int)Math.Round(box.Height * 0.30);
        var padY = (int)Math.Round(box.Height * 0.12);
        var padded = new CvRect(box.X - padX, box.Y - padY, box.Width + 2 * padX, box.Height + 2 * padY);
        var bounded = new CvRect(
            Math.Clamp(padded.X, 0, bgr.Cols - 1),
            Math.Clamp(padded.Y, 0, bgr.Rows - 1),
            Math.Clamp(padded.Width, 1, bgr.Cols - Math.Clamp(padded.X, 0, bgr.Cols - 1)),
            Math.Clamp(padded.Height, 1, bgr.Rows - Math.Clamp(padded.Y, 0, bgr.Rows - 1)));
        using var region = new Mat(bgr, bounded);
        var aspect = (double)bounded.Width / bounded.Height;
        var targetWidth = Math.Clamp((int)Math.Ceiling(RecInputHeight * aspect), 16, RecMaxInputWidth);
        using var resized = new Mat();
        Cv2.Resize(region, resized, new Size(targetWidth, RecInputHeight));
        var tensor = ToNormalizedChwTensor(resized, ImageNetNormalization: false);

        cancellationToken.ThrowIfCancellationRequested();
        using var results = _recSession.Run([NamedOnnxValue.CreateFromTensor(_recInputName, tensor)]);
        var output = (DenseTensor<float>)results.First().AsTensor<float>();
        var dimensions = output.Dimensions;
        var steps = dimensions[1];
        var classes = dimensions[2];
        var buffer = output.Buffer.Span;

        var builder = new System.Text.StringBuilder();
        double confidenceSum = 0;
        var emitted = 0;
        var lastIndex = 0;
        for (var step = 0; step < steps; step++)
        {
            var offset = step * classes;
            var bestIndex = 0;
            var bestScore = float.MinValue;
            for (var cls = 0; cls < classes; cls++)
            {
                var score = buffer[offset + cls];
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = cls;
                }
            }

            if (bestIndex != 0 && bestIndex != lastIndex)
            {
                builder.Append(MapCharacter(bestIndex));
                confidenceSum += bestScore;
                emitted++;
            }
            lastIndex = bestIndex;
        }

        return new RecognizedLine(builder.ToString(), emitted > 0 ? confidenceSum / emitted : 0);
    }

    /// <summary>Index 0 is the CTC blank; the trailing extra class is a space.</summary>
    private string MapCharacter(int index) =>
        index >= 1 && index <= _keys.Length ? _keys[index - 1] : " ";

    private static double BoxScore(Mat probability, Point[] contour)
    {
        var bounds = Cv2.BoundingRect(contour);
        bounds = bounds.Intersect(new CvRect(0, 0, probability.Cols, probability.Rows));
        if (bounds.Width < 1 || bounds.Height < 1) return 0;

        // Note: Mat.Zeros returns a MatExpr whose implicit Mat conversion makes
        // a fresh copy per use — FillPoly and Mean would each see a different
        // temporary. Materialize a single Mat explicitly.
        using var mask = new Mat(bounds.Height, bounds.Width, MatType.CV_8UC1, Scalar.Black);
        var shifted = contour.Select(point => new Point(point.X - bounds.X, point.Y - bounds.Y)).ToArray();
        Cv2.FillPoly(mask, [shifted], Scalar.White);
        using var region = new Mat(probability, bounds);
        return Cv2.Mean(region, mask).Val0;
    }

    /// <summary>
    /// Packs a BGR mat into a 1x3xHxW float tensor.  Channel order stays BGR to
    /// match the PaddleOCR inference pipeline the models were exported with.
    /// </summary>
    private static DenseTensor<float> ToNormalizedChwTensor(Mat bgr, bool ImageNetNormalization)
    {
        var height = bgr.Rows;
        var width = bgr.Cols;
        var pixels = new byte[height * width * 3];
        if (bgr.IsContinuous())
        {
            Marshal.Copy(bgr.Data, pixels, 0, pixels.Length);
        }
        else
        {
            for (var row = 0; row < height; row++)
                Marshal.Copy(bgr.Ptr(row), pixels, row * width * 3, width * 3);
        }

        // Detection uses ImageNet mean/std; recognition uses (x/255 - 0.5)/0.5.
        ReadOnlySpan<float> mean = ImageNetNormalization ? [0.485f, 0.456f, 0.406f] : [0.5f, 0.5f, 0.5f];
        ReadOnlySpan<float> std = ImageNetNormalization ? [0.229f, 0.224f, 0.225f] : [0.5f, 0.5f, 0.5f];

        var tensor = new DenseTensor<float>([1, 3, height, width]);
        var buffer = tensor.Buffer.Span;
        var planeSize = height * width;
        for (var channel = 0; channel < 3; channel++)
        {
            var channelMean = mean[channel];
            var channelStd = std[channel];
            var planeOffset = channel * planeSize;
            for (var pixel = 0; pixel < planeSize; pixel++)
                buffer[planeOffset + pixel] = (pixels[pixel * 3 + channel] / 255f - channelMean) / channelStd;
        }

        return tensor;
    }

    private static int RoundToMultipleOf32(double value) =>
        Math.Max(32, (int)Math.Round(value / 32) * 32);

    private static string First(IEnumerable<string> values) =>
        values.FirstOrDefault() ?? throw new InvalidDataException("ONNX 模型没有输入节点。");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _detSession.Dispose();
        _recSession.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PaddleOcrEngine));
    }
}
