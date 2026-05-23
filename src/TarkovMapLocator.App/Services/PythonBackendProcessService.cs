using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace TarkovMapLocator.App.Services;

public sealed class PythonBackendProcessService : IDisposable
{
    private const string BackendUrl = "http://127.0.0.1:5173/";
    private const string HealthUrl = "http://127.0.0.1:5173/api/health";
    private const int BackendPort = 5173;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(350);

    private readonly HttpClient httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    private Process? process;
    private bool disposed;

    public Uri EntryUri { get; } = new(BackendUrl);

    public async Task<Uri> StartAndWaitAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var initialProbe = await ProbeBackendAsync(cancellationToken).ConfigureAwait(false);
        if (initialProbe.IsReady)
        {
            return EntryUri;
        }

        if (initialProbe.IsMismatched)
        {
            throw new InvalidOperationException(
                $"端口 5173 已被另一个 TarkovMapLocator 后端占用: {initialProbe.ResourceRoot}。请先关闭旧版工具或残留的 pythonw.exe 后重试。");
        }

        if (!CanBindBackendPort())
        {
            throw new InvalidOperationException("端口 5173 已被其他程序占用。请关闭旧版工具、残留的 pythonw.exe 或占用该端口的程序后重试。");
        }

        StartProcess();
        try
        {
            await WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);
            return EntryUri;
        }
        catch
        {
            StopOwnedProcess();
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        httpClient.Dispose();

        StopOwnedProcess();
    }

    private void StopOwnedProcess()
    {
        if (process is null)
        {
            return;
        }

        var processToStop = process;
        process = null;
        try
        {
            if (!processToStop.HasExited)
            {
                processToStop.Kill(entireProcessTree: true);
                processToStop.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Exception)
        {
            try
            {
                processToStop.Dispose();
            }
            catch
            {
            }

            return;
        }

        processToStop.Dispose();
    }

    private void StartProcess()
    {
        var appRoot = AppContext.BaseDirectory;
        var pythonPath = Path.Combine(appRoot, "runtime", "python", "pythonw.exe");
        var launcherPath = Path.Combine(appRoot, "launcher.py");

        if (!File.Exists(pythonPath))
        {
            throw new FileNotFoundException("Bundled Python runtime was not found.", pythonPath);
        }

        if (!File.Exists(launcherPath))
        {
            throw new FileNotFoundException("Python launcher was not found.", launcherPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            WorkingDirectory = appRoot,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add(launcherPath);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(BackendPort.ToString(System.Globalization.CultureInfo.InvariantCulture));

        process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the Python backend process.");
    }

    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + StartupTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var probe = await ProbeBackendAsync(cancellationToken).ConfigureAwait(false);
            if (probe.IsReady)
            {
                return;
            }

            if (probe.IsMismatched)
            {
                throw new InvalidOperationException(
                    $"端口 5173 正在响应另一个安装目录的后端: {probe.ResourceRoot}。请关闭旧后端后重试。");
            }

            if (process is { HasExited: true })
            {
                throw new InvalidOperationException($"Python backend exited before {EntryUri} became available.");
            }

            await Task.Delay(ProbeInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out waiting for {EntryUri}.");
    }

    private async Task<BackendProbeResult> ProbeBackendAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, HealthUrl);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            var hasExpectedServerHeader = response.Headers.Server.Any(static value =>
                value.Product?.Name?.Contains("TarkovMapLocator", StringComparison.OrdinalIgnoreCase) == true);
            if (response.StatusCode is not HttpStatusCode.OK || !hasExpectedServerHeader)
            {
                return BackendProbeResult.NotReady;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var payload = JsonNode.Parse(body) as JsonObject;
            var resourceRoot = payload?["resourceRoot"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(resourceRoot))
            {
                return BackendProbeResult.NotReady;
            }

            return AreSameDirectory(AppContext.BaseDirectory, resourceRoot)
                ? BackendProbeResult.Ready(resourceRoot)
                : BackendProbeResult.Mismatched(resourceRoot);
        }
        catch
        {
            return BackendProbeResult.NotReady;
        }
    }

    private static bool AreSameDirectory(string first, string second)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool CanBindBackendPort()
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, BackendPort);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private sealed record BackendProbeResult(bool IsReady, bool IsMismatched, string ResourceRoot)
    {
        public static BackendProbeResult NotReady { get; } = new(false, false, "");

        public static BackendProbeResult Ready(string resourceRoot)
        {
            return new(true, false, resourceRoot);
        }

        public static BackendProbeResult Mismatched(string resourceRoot)
        {
            return new(false, true, resourceRoot);
        }
    }
}
