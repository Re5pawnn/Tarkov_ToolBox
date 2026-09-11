using System.Windows;

namespace TarkovMapLocatorDesktop.Utilities;

/// <summary>Small uniform-grid index used by marker label collision layout.</summary>
internal sealed class RectSpatialIndex(double cellSize = 96)
{
    private readonly Dictionary<long, List<int>> _cells = [];
    private readonly List<Rect> _rectangles = [];

    public bool Intersects(Rect candidate) => CountIntersections(candidate) > 0;

    public int CountIntersections(Rect candidate)
    {
        var seen = new HashSet<int>();
        var count = 0;
        foreach (var key in EnumerateKeys(candidate))
        {
            if (!_cells.TryGetValue(key, out var indices)) continue;
            foreach (var index in indices)
                if (seen.Add(index) && _rectangles[index].IntersectsWith(candidate))
                    count++;
        }
        return count;
    }

    public void Add(Rect rectangle)
    {
        var index = _rectangles.Count;
        _rectangles.Add(rectangle);
        foreach (var key in EnumerateKeys(rectangle))
        {
            if (!_cells.TryGetValue(key, out var indices)) _cells[key] = indices = [];
            indices.Add(index);
        }
    }

    private IEnumerable<long> EnumerateKeys(Rect rectangle)
    {
        var left = (int)Math.Floor(rectangle.Left / cellSize);
        var right = (int)Math.Floor(rectangle.Right / cellSize);
        var top = (int)Math.Floor(rectangle.Top / cellSize);
        var bottom = (int)Math.Floor(rectangle.Bottom / cellSize);
        for (var x = left; x <= right; x++)
            for (var y = top; y <= bottom; y++)
                yield return ((long)x << 32) ^ (uint)y;
    }
}
