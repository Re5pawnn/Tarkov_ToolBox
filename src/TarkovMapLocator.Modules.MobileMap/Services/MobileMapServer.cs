using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TarkovMapLocator.ModuleContracts;

namespace TarkovMapLocator.Modules.MobileMap.Services;

public sealed class MobileMapServer : IAsyncDisposable
{
    private const string AccessCookieName = "tarkov_mobile_token";
    private const int MaximumHeaderBytes = 16 * 1024;
    private const int MaximumBodyBytes = 256 * 1024;
    private const int MaximumTargetCharacters = 2048;
    private const int MaximumClients = 12;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly object _stateGate = new();
    private readonly object _snapshotGate = new();
    private readonly SemaphoreSlim _clientSlots = new(MaximumClients, MaximumClients);
    private readonly ConcurrentDictionary<int, Task> _clientTasks = new();
    private readonly string _webDirectory;
    private readonly Func<string, string, string?>? _teamSyncHandler;
    private IReadOnlyDictionary<string, FeatureMobileMapAsset> _assets =
        new Dictionary<string, FeatureMobileMapAsset>(StringComparer.Ordinal);
    private IReadOnlyList<TcpListener> _listeners = [];
    private CancellationTokenSource? _cancellation;
    private IReadOnlyList<Task> _acceptLoops = [];
    private string _token = string.Empty;
    private string _snapshotSignature = string.Empty;
    private string _snapshotJson = "{\"revision\":0,\"snapshot\":null}";
    private int _nextClientId;
    private int _connectedClients;
    private long _revision;

    public MobileMapServer(string? webDirectory = null, Func<string, string, string?>? teamSyncHandler = null)
    {
        _webDirectory = string.IsNullOrWhiteSpace(webDirectory)
            ? MobileMapRuntime.WebDirectory
            : Path.GetFullPath(webDirectory);
        _teamSyncHandler = teamSyncHandler;
    }

    public event EventHandler? StateChanged;

    public bool IsRunning
    {
        get
        {
            lock (_stateGate) return _listeners.Count > 0;
        }
    }

    public int Port { get; private set; }

    public string Token
    {
        get
        {
            lock (_stateGate) return _token;
        }
    }

    public int ConnectedClients => Volatile.Read(ref _connectedClients);

    public IReadOnlyList<string> AccessUrls { get; private set; } = [];

    public string PrimaryAccessUrl => AccessUrls.FirstOrDefault() ?? string.Empty;

    public void Start(int port, CancellationToken shutdownToken = default)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), "端口必须在 1024 到 65535 之间。");
        lock (_stateGate)
        {
            if (_listeners.Count > 0) return;

            var listeners = GetListenAddresses().Select(address => new TcpListener(address, port)).ToArray();
            try
            {
                foreach (var listener in listeners) listener.Start(32);
            }
            catch
            {
                foreach (var listener in listeners) try { listener.Stop(); } catch { }
                throw;
            }
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
            _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            Port = port;
            AccessUrls = BuildAccessUrls(port, _token, listeners.Select(listener => ((IPEndPoint)listener.LocalEndpoint).Address));
            _listeners = listeners;
            _cancellation = cancellation;
            _acceptLoops = listeners.Select(listener => AcceptLoopAsync(listener, cancellation.Token)).ToArray();
        }
        RaiseStateChanged();
    }

    public bool UpdateSnapshot(FeatureMobileMapSnapshot snapshot)
    {
        var assets = new Dictionary<string, FeatureMobileMapAsset>(StringComparer.Ordinal);
        var baseAssetKey = RegisterAsset(snapshot.BaseMap, assets);
        var layerAssetKey = RegisterAsset(snapshot.LayerMap, assets);
        var compactBaseAssetKey = RegisterAsset(snapshot.CompactBaseMap, assets);
        var sharpBaseAssetKey = RegisterAsset(snapshot.SharpBaseMap, assets);
        var compactLayerAssetKey = RegisterAsset(snapshot.CompactLayerMap, assets);
        var sharpLayerAssetKey = RegisterAsset(snapshot.SharpLayerMap, assets);
        var content = new MobileMapWireSnapshot(
            snapshot.MapId,
            snapshot.MapName,
            snapshot.MapStyle,
            snapshot.LayerId,
            snapshot.LayerName,
            baseAssetKey,
            layerAssetKey,
            compactBaseAssetKey,
            sharpBaseAssetKey,
            compactLayerAssetKey,
            sharpLayerAssetKey,
            snapshot.LayerDimsBaseMap,
            snapshot.IsListening,
            snapshot.HasLivePosition,
            snapshot.IsPositionStale,
            snapshot.PositionUpdatedAt,
            snapshot.Markers.Select(marker => new MobileMapWireMarker(
                marker.Id,
                marker.Type,
                marker.Label,
                Math.Clamp(marker.X, 0, 1),
                Math.Clamp(marker.Y, 0, 1),
                marker.HeadingDegrees,
                marker.ColorHex)).ToArray());
        var signature = JsonSerializer.Serialize(content, JsonOptions);

        lock (_snapshotGate)
        {
            _assets = assets;
            if (string.Equals(_snapshotSignature, signature, StringComparison.Ordinal)) return false;
            _snapshotSignature = signature;
            var revision = ++_revision;
            _snapshotJson = JsonSerializer.Serialize(new MobileMapWireEnvelope(revision, content), JsonOptions);
        }
        return true;
    }

    public async Task StopAsync()
    {
        IReadOnlyList<TcpListener> listeners;
        CancellationTokenSource? cancellation;
        IReadOnlyList<Task> acceptLoops;
        lock (_stateGate)
        {
            listeners = _listeners;
            cancellation = _cancellation;
            acceptLoops = _acceptLoops;
            _listeners = [];
            _cancellation = null;
            _acceptLoops = [];
            _token = string.Empty;
            AccessUrls = [];
            Port = 0;
        }

        if (listeners.Count == 0) return;
        try { cancellation?.Cancel(); } catch { }
        foreach (var listener in listeners) try { listener.Stop(); } catch { }
        if (acceptLoops.Count > 0)
        {
            try { await Task.WhenAll(acceptLoops).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { }
        }

        var clients = _clientTasks.Values.ToArray();
        if (clients.Length > 0)
        {
            try { await Task.WhenAll(clients).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { }
        }
        cancellation?.Dispose();
        Interlocked.Exchange(ref _connectedClients, 0);
        RaiseStateChanged();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _clientSlots.Dispose();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (SocketException) when (cancellationToken.IsCancellationRequested) { break; }

            // Do not retain sockets in an unbounded queue behind long-lived SSE streams.
            if (cancellationToken.IsCancellationRequested || !_clientSlots.Wait(0))
            {
                client.Dispose();
                continue;
            }

            var clientId = Interlocked.Increment(ref _nextClientId);
            var task = HandleClientGuardedAsync(client, cancellationToken);
            _clientTasks[clientId] = task;
            _ = task.ContinueWith(
                completedTask => _clientTasks.TryRemove(clientId, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task HandleClientGuardedAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using (client)
            {
                using var closeOnStop = cancellationToken.Register(client.Dispose);
                client.NoDelay = true;
                client.ReceiveTimeout = (int)RequestTimeout.TotalMilliseconds;
                client.SendTimeout = (int)RequestTimeout.TotalMilliseconds;
                await HandleClientAsync(client, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException) { }
        catch (SocketException) { }
        finally
        {
            _clientSlots.Release();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await using var stream = client.GetStream();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        var request = await ReadRequestAsync(stream, timeout.Token).ConfigureAwait(false);
        if (request is null)
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", "请求无效", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }

        var path = request.Uri.AbsolutePath;
        if (string.Equals(request.Method, "POST", StringComparison.Ordinal) && string.Equals(path, "/api/team-sync", StringComparison.Ordinal))
        {
            var remoteAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";
            var response = _teamSyncHandler?.Invoke(request.Body, remoteAddress);
            if (string.IsNullOrWhiteSpace(response))
                await WriteTextResponseAsync(stream, 503, "Service Unavailable", "队友共享房间未开启", "text/plain; charset=utf-8", cancellationToken, noStore: true).ConfigureAwait(false);
            else
                await WriteTextResponseAsync(stream, 200, "OK", response, "application/json; charset=utf-8", cancellationToken, noStore: true).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
        {
            await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "请求方法不受支持", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(path, "/app.css", StringComparison.Ordinal))
        {
            await WriteKnownFileAsync(stream, Path.Combine(_webDirectory, "app.css"), "text/css; charset=utf-8", false, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (string.Equals(path, "/app.js", StringComparison.Ordinal))
        {
            await WriteKnownFileAsync(stream, Path.Combine(_webDirectory, "app.js"), "text/javascript; charset=utf-8", false, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (string.Equals(path, "/favicon.ico", StringComparison.Ordinal))
        {
            await WriteEmptyResponseAsync(stream, 204, "No Content", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (path.StartsWith("/asset/", StringComparison.Ordinal))
        {
            if (!IsCookieAuthorized(request))
            {
                await WriteTextResponseAsync(stream, 403, "Forbidden", "访问码无效，请从电脑端重新复制地址。", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return;
            }
            var key = Uri.UnescapeDataString(path[7..]);
            IReadOnlyDictionary<string, FeatureMobileMapAsset> assets;
            lock (_snapshotGate) assets = _assets;
            if (!IsSafeAssetKey(key) || !assets.TryGetValue(key, out var asset))
            {
                await WriteTextResponseAsync(stream, 404, "Not Found", "地图资源不存在", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return;
            }
            await WriteKnownFileAsync(stream, asset.FilePath, asset.ContentType, true, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (path == "/" && IsQueryTokenAuthorized(request.Uri))
        {
            await WriteAccessCookieRedirectAsync(stream, Token, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!IsCookieAuthorized(request))
        {
            await WriteTextResponseAsync(stream, 403, "Forbidden", "访问码无效，请从电脑端重新复制地址。", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (path == "/")
        {
            await WriteKnownFileAsync(stream, Path.Combine(_webDirectory, "index.html"), "text/html; charset=utf-8", false, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (string.Equals(path, "/api/snapshot", StringComparison.Ordinal))
        {
            string json;
            lock (_snapshotGate) json = _snapshotJson;
            await WriteTextResponseAsync(stream, 200, "OK", json, "application/json; charset=utf-8", cancellationToken, noStore: true).ConfigureAwait(false);
            return;
        }
        if (string.Equals(path, "/api/events", StringComparison.Ordinal))
        {
            await StreamEventsAsync(stream, cancellationToken).ConfigureAwait(false);
            return;
        }
        await WriteTextResponseAsync(stream, 404, "Not Found", "页面不存在", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
    }

    private bool IsQueryTokenAuthorized(Uri uri) =>
        IsExpectedToken(ParseQuery(uri.Query).TryGetValue("token", out var value) ? value : string.Empty);

    private bool IsCookieAuthorized(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Cookie", out var header)) return false;
        foreach (var cookie in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = cookie.IndexOf('=');
            if (separator > 0 && string.Equals(cookie[..separator].Trim(), AccessCookieName, StringComparison.Ordinal) &&
                IsExpectedToken(cookie[(separator + 1)..].Trim())) return true;
        }
        return false;
    }

    private bool IsExpectedToken(string provided)
    {
        string expected;
        lock (_stateGate) expected = _token;
        if (provided.Length != expected.Length || provided.Length == 0) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));
    }

    private async Task StreamEventsAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var headers = "HTTP/1.1 200 OK\r\n" +
                      "Content-Type: text/event-stream; charset=utf-8\r\n" +
                      "Cache-Control: no-store\r\n" +
                      "Connection: keep-alive\r\n" +
                      SecurityHeaders + "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _connectedClients);
        RaiseStateChanged();
        long sentRevision = -1;
        var heartbeatAt = DateTimeOffset.UtcNow;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string json;
                long revision;
                lock (_snapshotGate)
                {
                    json = _snapshotJson;
                    revision = _revision;
                }

                if (revision != sentRevision)
                {
                    var payload = Encoding.UTF8.GetBytes($"event: snapshot\ndata: {json}\n\n");
                    await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    sentRevision = revision;
                    heartbeatAt = DateTimeOffset.UtcNow;
                }
                else if (DateTimeOffset.UtcNow - heartbeatAt >= TimeSpan.FromSeconds(12))
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(": keepalive\n\n"), cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    heartbeatAt = DateTimeOffset.UtcNow;
                }
                await Task.Delay(350, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _connectedClients);
            RaiseStateChanged();
        }
    }

    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaximumHeaderBytes];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count, buffer.Length - count), cancellationToken).ConfigureAwait(false);
            if (read == 0) return null;
            count += read;
            var headerEnd = FindHeaderEnd(buffer, count);
            if (headerEnd < 0) continue;

            var headerText = Encoding.ASCII.GetString(buffer, 0, headerEnd);
            var firstLineEnd = headerText.IndexOf("\r\n", StringComparison.Ordinal);
            var requestLine = firstLineEnd >= 0 ? headerText[..firstLineEnd] : headerText;
            var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || parts[1].Length > MaximumTargetCharacters || !parts[1].StartsWith('/')) return null;
            if (!Uri.TryCreate("http://localhost" + parts[1], UriKind.Absolute, out var uri)) return null;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in headerText[(firstLineEnd < 0 ? headerText.Length : firstLineEnd + 2)..].Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = line.IndexOf(':');
                if (separator > 0) headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
            if (headers.ContainsKey("Transfer-Encoding")) return null;
            var contentLength = 0;
            if (headers.TryGetValue("Content-Length", out var contentLengthText) &&
                (!int.TryParse(contentLengthText, out contentLength) || contentLength is < 0 or > MaximumBodyBytes))
                return null;

            var body = new byte[contentLength];
            var bodyStart = headerEnd + 4;
            var bodyRead = Math.Min(contentLength, Math.Max(0, count - bodyStart));
            if (bodyRead > 0) Buffer.BlockCopy(buffer, bodyStart, body, 0, bodyRead);
            while (bodyRead < contentLength)
            {
                var readBody = await stream.ReadAsync(body.AsMemory(bodyRead, contentLength - bodyRead), cancellationToken).ConfigureAwait(false);
                if (readBody == 0) return null;
                bodyRead += readBody;
            }
            return new HttpRequest(parts[0], uri, headers, Encoding.UTF8.GetString(body));
        }
        return null;
    }

    private static int FindHeaderEnd(byte[] bytes, int count)
    {
        for (var index = 3; index < count; index++)
            if (bytes[index - 3] == '\r' && bytes[index - 2] == '\n' && bytes[index - 1] == '\r' && bytes[index] == '\n')
                return index - 3;
        return -1;
    }

    private static async Task WriteKnownFileAsync(
        NetworkStream stream,
        string path,
        string contentType,
        bool cache,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            await WriteTextResponseAsync(stream, 404, "Not Found", "资源不存在", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }

        var info = new FileInfo(path);
        var headers = BuildHeaders(200, "OK", contentType, info.Length, cache ? "private, max-age=31536000, immutable" : "no-store");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken).ConfigureAwait(false);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await file.CopyToAsync(stream, 64 * 1024, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteTextResponseAsync(
        NetworkStream stream,
        int statusCode,
        string reason,
        string content,
        string contentType,
        CancellationToken cancellationToken,
        bool noStore = false)
    {
        var body = Encoding.UTF8.GetBytes(content);
        var headers = BuildHeaders(statusCode, reason, contentType, body.Length, noStore ? "no-store" : "no-cache");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    private static Task WriteEmptyResponseAsync(NetworkStream stream, int statusCode, string reason, CancellationToken cancellationToken) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(BuildHeaders(statusCode, reason, "text/plain", 0, "no-store")), cancellationToken).AsTask();

    private static Task WriteAccessCookieRedirectAsync(NetworkStream stream, string token, CancellationToken cancellationToken)
    {
        var response = "HTTP/1.1 303 See Other\r\n" +
                       "Location: /\r\n" +
                       $"Set-Cookie: {AccessCookieName}={token}; HttpOnly; SameSite=Strict; Path=/\r\n" +
                       "Content-Length: 0\r\nCache-Control: no-store\r\nConnection: close\r\n" +
                       SecurityHeaders + "\r\n";
        return stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken).AsTask();
    }

    private static string BuildHeaders(int statusCode, string reason, string contentType, long contentLength, string cacheControl) =>
        $"HTTP/1.1 {statusCode} {reason}\r\n" +
        $"Content-Type: {contentType}\r\n" +
        $"Content-Length: {contentLength}\r\n" +
        $"Cache-Control: {cacheControl}\r\n" +
        "Connection: close\r\n" +
        SecurityHeaders + "\r\n";

    private const string SecurityHeaders =
        "X-Content-Type-Options: nosniff\r\n" +
        "Referrer-Policy: no-referrer\r\n" +
        "Content-Security-Policy: default-src 'self'; img-src 'self' data: blob:; connect-src 'self'; style-src 'self'; script-src 'self'; base-uri 'none'; frame-ancestors 'none'\r\n";

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator]);
            var value = Uri.UnescapeDataString(separator < 0 ? string.Empty : pair[(separator + 1)..]);
            values[key] = value;
        }
        return values;
    }

    private static string? RegisterAsset(
        FeatureMobileMapAsset? asset,
        IDictionary<string, FeatureMobileMapAsset> assets)
    {
        if (asset is null || !File.Exists(asset.FilePath) || !IsSafeAssetKey(asset.Key)) return null;
        var ticks = File.GetLastWriteTimeUtc(asset.FilePath).Ticks.ToString("x");
        var publicKey = $"{asset.Key}-{ticks}";
        if (!IsSafeAssetKey(publicKey)) return null;
        assets[publicKey] = asset;
        return publicKey;
    }

    private static bool IsSafeAssetKey(string value) =>
        value.Length is > 0 and <= 160 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static IReadOnlyList<IPAddress> GetListenAddresses()
    {
        var privateAddress = NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up &&
                           item.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet)
            .SelectMany(item => item.GetIPProperties().UnicastAddresses)
            .Select(item => item.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork && IsPrivateAddress(address))
            .Distinct()
            .OrderBy(address => address.ToString(), StringComparer.Ordinal)
            .FirstOrDefault();
        return privateAddress is null ? [IPAddress.Loopback] : [IPAddress.Loopback, privateAddress];
    }

    private static IReadOnlyList<string> BuildAccessUrls(int port, string token, IEnumerable<IPAddress> listenAddresses)
    {
        var addresses = listenAddresses.Where(address => !IPAddress.IsLoopback(address)).ToArray();
        if (addresses.Length == 0) addresses = [IPAddress.Loopback];
        return addresses.Select(address => $"http://{address}:{port}/?token={token}").ToArray();
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               bytes[0] == 192 && bytes[1] == 168 ||
               bytes[0] == 172 && bytes[1] is >= 16 and <= 31;
    }

    private void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(this, EventArgs.Empty); }
        catch { }
    }

    private sealed record HttpRequest(string Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string Body);

    private sealed record MobileMapWireEnvelope(long Revision, MobileMapWireSnapshot Snapshot);

    private sealed record MobileMapWireSnapshot(
        string? MapId,
        string? MapName,
        string MapStyle,
        string? LayerId,
        string LayerName,
        string? BaseAssetKey,
        string? LayerAssetKey,
        string? CompactBaseAssetKey,
        string? SharpBaseAssetKey,
        string? CompactLayerAssetKey,
        string? SharpLayerAssetKey,
        bool LayerDimsBaseMap,
        bool IsListening,
        bool HasLivePosition,
        bool IsPositionStale,
        DateTimeOffset? PositionUpdatedAt,
        IReadOnlyList<MobileMapWireMarker> Markers);

    private sealed record MobileMapWireMarker(
        string Id,
        string Type,
        string Label,
        double X,
        double Y,
        double? HeadingDegrees,
        string? ColorHex);
}
