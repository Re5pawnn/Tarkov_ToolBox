using System.Collections.Concurrent;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovMapLocator.ModuleContracts;
using TarkovMapLocator.Modules.TeamSync.Models;

namespace TarkovMapLocator.Modules.TeamSync.Services;

/// <summary>
/// Signed room synchronisation carried by the mobile map HTTP listener. A host
/// returns a full peer snapshot on each update, so every teammate sees the room.
/// </summary>
public sealed class LanPeerSyncService : IAsyncDisposable
{
    private const int ProtocolVersion = 2;
    private const int SocketTimeoutMilliseconds = 4000;
    private const int MaxFrameCharacters = 256 * 1024;
    private static readonly TimeSpan PeerTtl = TimeSpan.FromSeconds(8);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly object _stateGate = new();
    private readonly IFeatureHost _host;
    private readonly ConcurrentDictionary<string, PeerWire> _peers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _acceptedRequestMacs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _joinGate = new(1, 1);
    private readonly HttpClient _httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly object _addressGate = new();
    private readonly string _instanceId;
    private CancellationTokenSource? _cancellation;
    private LanSyncConfig _config = LanSyncConfig.Default;
    private LocalStateWire _localState = LocalStateWire.Empty;
    private string _mode = "off";
    private string _status = "未开启同步";
    private string _lastError = "";
    private int _sessionId;
    private string _cachedLocalAddress = "127.0.0.1";
    private string _boundLocalAddress = "127.0.0.1";
    private long _localAddressCacheTick;

    public LanPeerSyncService(IFeatureHost host)
    {
        _host = host;
        _instanceId = ReadOrCreateInstanceId(host.DataDirectory);
    }

    public bool IsRunning
    {
        get
        {
            lock (_stateGate) return _cancellation is not null;
        }
    }

    public bool IsHosting
    {
        get
        {
            lock (_stateGate) return _cancellation is not null && _mode == "host";
        }
    }

    public async Task StartHostAsync(LanSyncConfig config, CancellationToken cancellationToken = default)
    {
        var normalized = config.Normalize() with { Enabled = true, Mode = "host" };
        await StopAsync("正在开启房间");
        if (normalized.RoomKey.Length < 12)
        {
            SetStartFailure(normalized, "房间密钥至少需要 12 位");
            return;
        }

        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_stateGate)
        {
            _sessionId++;
            _config = normalized;
            _boundLocalAddress = GetCachedLocalAddress();
            _cancellation = source;
            _mode = "host";
            _lastError = "";
            _status = "房间已开启，等待队友加入";
        }
    }

    public async Task StartJoinAsync(LanSyncConfig config, CancellationToken cancellationToken = default)
    {
        var normalized = config.Normalize() with { Enabled = true, Mode = "join" };
        await StopAsync("正在连接房主");
        if (normalized.RoomKey.Length < 12)
        {
            SetStartFailure(normalized, "房间密钥至少需要 12 位");
            return;
        }
        if (!TryBuildSyncUri(normalized.RemoteEndpoint, out _))
        {
            lock (_stateGate)
            {
                _config = normalized with { Enabled = false };
                _mode = "off";
                _lastError = "Invalid remote endpoint";
                _status = "地址格式不对，请输入 IP:端口或 HTTPS 地址";
            }
            return;
        }

        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_stateGate)
        {
            _sessionId++;
            _config = normalized;
            _cancellation = source;
            _mode = "join";
            _lastError = "";
            _status = "正在连接房主";
        }
    }

    public Task StopAsync(string nextStatus = "未开启同步")
    {
        CancellationTokenSource? source;
        lock (_stateGate)
        {
            _sessionId++;
            source = _cancellation;
            _cancellation = null;
            _mode = "off";
            _status = nextStatus;
            _lastError = "";
            _localState = LocalStateWire.Empty;
        }

        try { source?.Cancel(); } catch { }
        _peers.Clear();
        _acceptedRequestMacs.Clear();
        source?.Dispose();
        return Task.CompletedTask;
    }

    public async Task PublishAsync(LanSyncConfig config, FeatureSharedLocation location, CancellationToken cancellationToken = default)
    {
        string mode;
        CancellationToken serviceToken;
        lock (_stateGate)
        {
            if (_cancellation is null) return;
            _config = config.Normalize();
            _localState = LocalStateWire.FromLocal(location);
            mode = _mode;
            serviceToken = _cancellation.Token;
        }

        TrimExpiredPeers();
        if (mode == "host")
        {
            UpdateHostStatus();
            return;
        }

        if (mode == "join") await SyncJoinOnceAsync(cancellationToken.CanBeCanceled ? cancellationToken : serviceToken);
    }

    public IReadOnlyList<FeatureTeamPeerPosition> BuildPeerPositions(string? currentMapId)
    {
        if (!IsRunning || string.IsNullOrWhiteSpace(currentMapId)) return [];
        TrimExpiredPeers();
        return ActivePeers()
            .Where(peer => string.Equals(peer.State.MapId, currentMapId, StringComparison.OrdinalIgnoreCase))
            .Where(peer => peer.State.X is not null && peer.State.Z is not null)
            .Select(peer => new FeatureTeamPeerPosition(
                peer.SenderId,
                peer.DisplayName,
                LanSyncConfig.NormalizeColor(peer.Color),
                peer.State.MapId!,
                peer.State.X!.Value,
                peer.State.Y,
                peer.State.Z!.Value,
                peer.State.YawDeg ?? 0,
                DateTimeOffset.FromUnixTimeMilliseconds(peer.LastSeenAt)))
            .ToArray();
    }

    public LanSyncUiState BuildUiState(string? currentMapId)
    {
        TrimExpiredPeers();
        LanSyncConfig config;
        string mode;
        string status;
        string lastError;
        bool running;
        string localAddress;
        lock (_stateGate)
        {
            config = _config;
            mode = _mode;
            status = _status;
            lastError = _lastError;
            running = _cancellation is not null;
            localAddress = _boundLocalAddress;
        }

        var peers = ActivePeers()
            .Select(peer => new LanPeerListItem(
                peer.DisplayName,
                LanSyncConfig.NormalizeColor(peer.Color),
                string.IsNullOrWhiteSpace(peer.State.MapName) ? "等待地图" : peer.State.MapName,
                FormatLastSeen(peer.LastSeenAt),
                !string.IsNullOrWhiteSpace(currentMapId) && string.Equals(peer.State.MapId, currentMapId, StringComparison.OrdinalIgnoreCase) && peer.State.X is not null && peer.State.Z is not null,
                peer.State.X is not null && peer.State.Z is not null))
            .OrderBy(peer => peer.DisplayName, StringComparer.CurrentCulture)
            .ToArray();

        return new LanSyncUiState(
            mode,
            status,
            running,
            $"{localAddress}:{config.SyncPort}",
            lastError,
            peers);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _httpClient.Dispose();
    }

    public string? HandleHostRequest(string requestJson, string remoteAddress)
    {
        LanSyncConfig config;
        int sessionId;
        lock (_stateGate)
        {
            if (_mode != "host" || _cancellation is null) return null;
            config = _config;
            sessionId = _sessionId;
        }

        try
        {
            var signedPacket = JsonSerializer.Deserialize<SignedSyncUpdatePacket>(requestJson, JsonOptions);
            var packet = signedPacket?.Payload;
            var authenticated = signedPacket is not null && packet is not null &&
                                IsValidPacket(packet) && VerifyMac(packet, signedPacket.Mac, config.RoomKey) &&
                                AcceptFreshRequest(signedPacket.Mac, packet.SentAt);
            if (authenticated && packet is not null)
            {
                lock (_stateGate)
                    if (_mode != "host" || _cancellation is null || _sessionId != sessionId) return null;
                _peers[packet.SenderId] = new PeerWire(
                    packet.SenderId,
                    LanSyncConfig.NormalizeDisplayName(packet.DisplayName),
                    LanSyncConfig.NormalizeColor(packet.Color),
                    remoteAddress,
                    0,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    packet.State?.Normalize() ?? LocalStateWire.Empty);
                UpdateHostStatus();
            }

            var response = authenticated
                ? BuildHostSnapshot()
                : new SyncSnapshotPacket("sync_error", ProtocolVersion, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), [], "authentication_failed");
            return JsonSerializer.Serialize(Sign(response, config.RoomKey), JsonOptions);
        }
        catch (JsonException)
        {
            var response = new SyncSnapshotPacket("sync_error", ProtocolVersion, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), [], "invalid_request");
            return JsonSerializer.Serialize(Sign(response, config.RoomKey), JsonOptions);
        }
    }

    private async Task SyncJoinOnceAsync(CancellationToken token)
    {
        await _joinGate.WaitAsync(token);
        try
        {
            LanSyncConfig config;
            LocalStateWire localState;
            int sessionId;
            lock (_stateGate)
            {
                if (_cancellation is null || _mode != "join") return;
                config = _config;
                localState = _localState;
                sessionId = _sessionId;
            }

            if (!TryBuildSyncUri(config.RemoteEndpoint, out var endpoint))
            {
                SetJoinFailure("地址格式不对，请输入 IP:端口或 HTTPS 地址");
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(SocketTimeoutMilliseconds);
            var request = new SyncUpdatePacket(
                "sync_update",
                ProtocolVersion,
                _instanceId,
                LanSyncConfig.NormalizeDisplayName(config.DisplayName),
                LanSyncConfig.NormalizeColor(config.Color),
                localState,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            using var content = new StringContent(JsonSerializer.Serialize(Sign(request, config.RoomKey), JsonOptions), Encoding.UTF8, "application/json");
            using var httpResponse = await _httpClient.PostAsync(endpoint, content, timeout.Token).ConfigureAwait(false);
            if (!httpResponse.IsSuccessStatusCode)
                throw new InvalidDataException($"房主返回 HTTP {(int)httpResponse.StatusCode}。");
            await using var stream = await httpResponse.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var responseLine = await ReadBoundedFrameAsync(stream, timeout.Token);
            if (string.IsNullOrWhiteSpace(responseLine)) throw new InvalidDataException("房主没有返回同步快照。");
            var signedResponse = JsonSerializer.Deserialize<SignedSyncSnapshotPacket>(responseLine, JsonOptions);
            var response = signedResponse?.Payload;
            if (signedResponse is null || response is null || !VerifyMac(response, signedResponse.Mac, config.RoomKey))
                throw new InvalidDataException("房间密钥不正确或同步响应已被篡改。");
            if (string.Equals(response.Type, "sync_error", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(response.Message ?? "房主拒绝了同步请求。");
            if (!string.Equals(response.Type, "sync_snapshot", StringComparison.OrdinalIgnoreCase) || response.Peers is null) throw new InvalidDataException("房主快照缺少队友数据。");

            lock (_stateGate)
                if (_mode != "join" || _cancellation is null || _sessionId != sessionId) return;
            ReplacePeers(response.Peers, response.ServerTime);
            lock (_stateGate)
            {
                if (_mode == "join")
                {
                    _lastError = "";
                    _status = "已连接到房主";
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            _peers.Clear();
            SetJoinFailure("连接失败: 请求超时");
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or JsonException or InvalidDataException)
        {
            _peers.Clear();
            SetJoinFailure($"连接失败: {exception.Message}");
        }
        finally
        {
            _joinGate.Release();
        }
    }

    private SyncSnapshotPacket BuildHostSnapshot()
    {
        LanSyncConfig config;
        LocalStateWire localState;
        string mode;
        lock (_stateGate)
        {
            config = _config;
            localState = _localState;
            mode = _mode;
        }

        if (mode != "host") return new SyncSnapshotPacket("sync_error", ProtocolVersion, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), [], "host_disabled");

        TrimExpiredPeers();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var peers = new List<PeerWire>
        {
            new(
                _instanceId,
                LanSyncConfig.NormalizeDisplayName(config.DisplayName),
                LanSyncConfig.NormalizeColor(config.Color),
                "host",
                config.SyncPort,
                now,
                localState)
        };
        peers.AddRange(ActivePeers());
        return new SyncSnapshotPacket("sync_snapshot", ProtocolVersion, now, peers, null);
    }

    private void ReplacePeers(IEnumerable<PeerWire> peers, long serverTime)
    {
        _peers.Clear();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var peer in peers)
        {
            if (string.IsNullOrWhiteSpace(peer.SenderId) || string.Equals(peer.SenderId, _instanceId, StringComparison.OrdinalIgnoreCase)) continue;
            _peers[peer.SenderId] = peer with
            {
                DisplayName = LanSyncConfig.NormalizeDisplayName(peer.DisplayName),
                Color = LanSyncConfig.NormalizeColor(peer.Color),
                LastSeenAt = now - Math.Max(0, serverTime - (peer.LastSeenAt > 0 ? peer.LastSeenAt : serverTime)),
                State = peer.State?.Normalize() ?? LocalStateWire.Empty
            };
        }
    }

    private IReadOnlyList<PeerWire> ActivePeers()
    {
        if (!IsRunning) return [];
        TrimExpiredPeers();
        return _peers.Values
            .Where(peer => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - peer.LastSeenAt <= PeerTtl.TotalMilliseconds)
            .OrderBy(peer => peer.DisplayName, StringComparer.CurrentCulture)
            .ToArray();
    }

    private void TrimExpiredPeers()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var peer in _peers)
        {
            if (now - peer.Value.LastSeenAt > PeerTtl.TotalMilliseconds) _peers.TryRemove(peer.Key, out _);
        }
    }

    private void UpdateHostStatus()
    {
        lock (_stateGate)
        {
            if (_mode != "host" || _cancellation is null) return;
            var peerCount = _peers.Count;
            _status = peerCount == 0 ? "房间已开启，等待队友加入" : $"已连接 {peerCount} 个队友";
        }
    }

    private void SetJoinFailure(string message)
    {
        lock (_stateGate)
        {
            if (_mode != "join" || _cancellation is null) return;
            _lastError = message;
            _status = message;
        }
    }

    private void SetStartFailure(LanSyncConfig config, string message)
    {
        lock (_stateGate)
        {
            _config = config with { Enabled = false };
            _mode = "off";
            _lastError = message;
            _status = message;
        }
    }

    private static bool IsValidPacket(SyncUpdatePacket packet) =>
        string.Equals(packet.Type, "sync_update", StringComparison.OrdinalIgnoreCase) &&
        packet.Version == ProtocolVersion &&
        !string.IsNullOrWhiteSpace(packet.SenderId) &&
        packet.SenderId.Length <= 64;

    private bool AcceptFreshRequest(string mac, long sentAt)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (Math.Abs(now - sentAt) > 30_000 || !_acceptedRequestMacs.TryAdd(mac, now)) return false;
        foreach (var accepted in _acceptedRequestMacs)
            if (now - accepted.Value > 60_000) _acceptedRequestMacs.TryRemove(accepted.Key, out _);
        return true;
    }

    private static SignedSyncUpdatePacket Sign(SyncUpdatePacket payload, string roomKey) =>
        new(payload, ComputeMac(payload, roomKey));

    private static SignedSyncSnapshotPacket Sign(SyncSnapshotPacket payload, string roomKey) =>
        new(payload, ComputeMac(payload, roomKey));

    private static string ComputeMac<T>(T payload, string roomKey) =>
        Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(roomKey),
            JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions))).ToLowerInvariant();

    private static bool VerifyMac<T>(T payload, string? providedMac, string roomKey)
    {
        if (providedMac is not { Length: 64 } || !providedMac.All(Uri.IsHexDigit)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(providedMac),
            Convert.FromHexString(ComputeMac(payload, roomKey)));
    }

    private static bool TryBuildSyncUri(string? endpoint, out Uri uri)
    {
        uri = null!;
        var text = endpoint?.Trim() ?? "";
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(parsed.Host) ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            parsed.Port is < 1 or > 65535) return false;
        uri = new UriBuilder(parsed) { Path = "/api/team-sync", Query = "", Fragment = "" }.Uri;
        return true;
    }

    private static string FormatLastSeen(long timestampMilliseconds)
    {
        var seconds = Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - timestampMilliseconds) / 1000d);
        return seconds < 1 ? "刚刚" : $"{seconds:0} 秒前";
    }

    private static string GetPreferredLocalAddress()
    {
        var physicalAddresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up)
            .Where(item => item.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
            .Where(item => !LooksVirtual(item))
            .SelectMany(item => item.GetIPProperties().UnicastAddresses)
            .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(item => item.Address.ToString())
            .Where(address => !IPAddress.IsLoopback(IPAddress.Parse(address)))
            .ToArray();
        if (physicalAddresses.FirstOrDefault(IsPrivateAddress) is { } physicalAddress) return physicalAddress;

        var fallbackAddresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up)
            .SelectMany(item => item.GetIPProperties().UnicastAddresses)
            .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(item => item.Address.ToString())
            .Where(address => !IPAddress.IsLoopback(IPAddress.Parse(address)))
            .ToArray();
        return fallbackAddresses.FirstOrDefault(IsPrivateAddress) ?? "127.0.0.1";
    }

    private string GetCachedLocalAddress()
    {
        var now = Environment.TickCount64;
        lock (_addressGate)
        {
            if (now - _localAddressCacheTick < 30_000 && _localAddressCacheTick != 0) return _cachedLocalAddress;
            _cachedLocalAddress = GetPreferredLocalAddress();
            _localAddressCacheTick = now;
            return _cachedLocalAddress;
        }
    }

    private static async Task<string?> ReadBoundedFrameAsync(Stream stream, CancellationToken token)
    {
        const int maxBytesPerCharacter = 4;
        var maximumBytes = MaxFrameCharacters * maxBytesPerCharacter;
        var rented = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            using var buffer = new MemoryStream(capacity: Math.Min(maximumBytes, 16 * 1024));
            while (true)
            {
                var read = await stream.ReadAsync(rented.AsMemory(0, rented.Length), token).ConfigureAwait(false);
                if (read == 0) return buffer.Length == 0 ? null : Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length)).TrimEnd('\r');

                var newline = Array.IndexOf(rented, (byte)'\n', 0, read);
                var count = newline >= 0 ? newline : read;
                if (buffer.Length + count > maximumBytes) throw new InvalidDataException("同步数据超过允许的最大长度。");
                buffer.Write(rented, 0, count);
                if (newline >= 0)
                {
                    var value = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length)).TrimEnd('\r');
                    if (value.Length > MaxFrameCharacters) throw new InvalidDataException("同步数据超过允许的最大长度。");
                    return value;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static bool LooksVirtual(NetworkInterface item)
    {
        var text = $"{item.Name} {item.Description}".ToLowerInvariant();
        return text.Contains("virtual", StringComparison.Ordinal) ||
               text.Contains("vmware", StringComparison.Ordinal) ||
               text.Contains("hyper-v", StringComparison.Ordinal) ||
               text.Contains("vbox", StringComparison.Ordinal) ||
               text.Contains("virtualbox", StringComparison.Ordinal) ||
               text.Contains("loopback", StringComparison.Ordinal);
    }

    private static bool IsPrivateAddress(string address)
    {
        var parts = address.Split('.');
        return parts.Length == 4 && int.TryParse(parts[0], out var first) && int.TryParse(parts[1], out var second) &&
               (first == 10 || first == 192 && second == 168 || first == 172 && second is >= 16 and <= 31);
    }

    private static string ReadOrCreateInstanceId(string directory)
    {
        var path = Path.Combine(directory, "sync-client-id.json");
        try
        {
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("clientId", out var value))
                {
                    var existing = value.GetString()?.Trim();
                    if (existing is { Length: 32 } && existing.All(Uri.IsHexDigit)) return existing.ToLowerInvariant();
                }
            }

            var created = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(directory);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { clientId = created }, JsonOptions));
            File.Move(temporary, path, overwrite: true);
            return created;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return Guid.NewGuid().ToString("N");
        }
    }

    private sealed record SyncUpdatePacket(
        string Type,
        int Version,
        string SenderId,
        string DisplayName,
        string Color,
        LocalStateWire State,
        long SentAt);

    private sealed record SignedSyncUpdatePacket(SyncUpdatePacket Payload, string Mac);

    private sealed record SyncSnapshotPacket(
        string Type,
        int Version,
        long ServerTime,
        IReadOnlyList<PeerWire> Peers,
        string? Message);

    private sealed record SignedSyncSnapshotPacket(SyncSnapshotPacket Payload, string Mac);

    private sealed record PeerWire(
        string SenderId,
        string DisplayName,
        string Color,
        string Host,
        int Port,
        long LastSeenAt,
        LocalStateWire State);

    private sealed record LocalStateWire(
        string? MapId,
        string? MapName,
        double? X,
        double? Y,
        double? Z,
        double? YawDeg,
        string? RaidStatus,
        long UpdatedAt)
    {
        public static LocalStateWire Empty { get; } = new(null, null, null, null, null, null, null, 0);

        public static LocalStateWire FromLocal(FeatureSharedLocation location) => new(
            location.MapId,
            location.MapName,
            location.X,
            location.Y,
            location.Z,
            location.YawDegrees is null ? null : location.YawDegrees % 360,
            location.X is null || location.Z is null ? "等待坐标" : "已更新位置",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        public LocalStateWire Normalize() => new(
            Trim(MapId, 64),
            Trim(MapName, 80),
            NormalizeNumber(X),
            NormalizeNumber(Y),
            NormalizeNumber(Z),
            NormalizeYaw(YawDeg),
            Trim(RaidStatus, 40),
            UpdatedAt > 0 ? UpdatedAt : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        private static string? Trim(string? value, int maxLength)
        {
            var text = value?.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text.Length <= maxLength ? text : text[..maxLength];
        }

        private static double? NormalizeNumber(double? value) => value is { } number && double.IsFinite(number) ? number : null;
        private static double? NormalizeYaw(double? value) => value is { } number && double.IsFinite(number) ? (number % 360 + 360) % 360 : null;
    }
}
