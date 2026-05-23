using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovMapLocator.App.Models;
using TarkovMapLocator.Core.Maps;

namespace TarkovMapLocator.App.Services;

public sealed class LanPeerSyncService : IAsyncDisposable
{
    private const int DefaultPort = 39247;
    private static readonly TimeSpan PeerTtl = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, PeerConnection> connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PeerState> peers = new(StringComparer.Ordinal);
    private readonly string instanceId = Guid.NewGuid().ToString("N");
    private CancellationTokenSource? cancellation;
    private TcpListener? listener;
    private string mode = "off";
    private string status = "未开启同步";
    private string lastError = "";
    private string remoteEndpoint = "";
    private PeerState? localState;

    public bool IsRunning => cancellation is not null;

    public IReadOnlyList<PeerState> ActivePeers => peers.Values
        .Where(peer => DateTimeOffset.UtcNow - peer.LastSeenAt <= PeerTtl)
        .OrderBy(peer => peer.DisplayName, StringComparer.CurrentCulture)
        .ToArray();

    public async Task StartHostAsync(NativeLanSyncRequest request, CancellationToken externalToken)
    {
        await StopAsync("正在开启房间");
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        mode = "host";
        lastError = "";
        remoteEndpoint = "";

        try
        {
            listener = new TcpListener(IPAddress.Any, DefaultPort);
            listener.Start();
            status = "房间已开启，等待队友加入";
            _ = AcceptLoopAsync(cancellation.Token);
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
            await StopAsync("端口被占用，请关闭其他同步或稍后再试", clearLastError: false);
        }
    }

    public async Task StartJoinAsync(NativeLanSyncRequest request, CancellationToken externalToken)
    {
        await StopAsync("正在连接队友");
        var endpoint = ParseEndpoint(request.RemoteEndpoint);
        if (string.IsNullOrWhiteSpace(endpoint.Host) || endpoint.Port <= 0)
        {
            mode = "off";
            status = "地址格式不对，请输入 IP:端口";
            lastError = "Invalid remote endpoint";
            remoteEndpoint = request.RemoteEndpoint.Trim();
            return;
        }

        cancellation = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        mode = "join";
        status = "正在连接队友";
        lastError = "";
        remoteEndpoint = $"{endpoint.Host}:{endpoint.Port}";
        _ = ConnectLoopAsync(endpoint.Host, endpoint.Port, cancellation.Token);
    }

    public Task StopAsync()
    {
        return StopAsync("未开启同步");
    }

    private Task StopAsync(string nextStatus, bool clearLastError = true)
    {
        var source = cancellation;
        cancellation = null;
        mode = "off";
        status = nextStatus;
        if (clearLastError)
        {
            lastError = "";
        }
        try
        {
            source?.Cancel();
        }
        catch
        {
        }
        finally
        {
            source?.Dispose();
        }

        try
        {
            listener?.Stop();
        }
        catch
        {
        }

        listener = null;
        foreach (var connection in connections.Values)
        {
            connection.Dispose();
        }

        connections.Clear();
        localState = null;
        return Task.CompletedTask;
    }

    public async Task PublishAsync(NativeLanSyncRequest request, MapPrototypeModel map, ScreenshotCoordinate? coordinate)
    {
        if (!IsRunning)
        {
            return;
        }

        var state = PeerState.FromLocal(instanceId, request, map, coordinate);
        localState = state;
        var json = JsonSerializer.Serialize(state, LanPeerSyncJsonContext.Default.PeerState);
        var payload = Encoding.UTF8.GetBytes(json + "\n");
        foreach (var connection in connections.Values.ToArray())
        {
            await connection.SendAsync(payload);
        }

        TrimExpiredPeers();
        UpdateConnectedStatus(hasLocalCoordinate: coordinate is not null);
    }

    public IReadOnlyList<NativePeerMarker> BuildPeerMarkers(MapPrototypeModel map)
    {
        TrimExpiredPeers();
        return ActivePeers
            .Where(peer => string.Equals(peer.MapId, map.Id, StringComparison.OrdinalIgnoreCase))
            .Where(peer => peer.X is not null && peer.Z is not null)
            .Select(peer =>
            {
                var projected = MapProjection.ProjectWorldToUnit(map.Bounds, peer.X!.Value, peer.Z!.Value);
                return MapProjection.IsUnitRange(projected.U) && MapProjection.IsUnitRange(projected.V)
                    ? new NativePeerMarker(
                        peer.InstanceId,
                        peer.DisplayName,
                        peer.Color,
                        projected.U,
                        projected.V,
                        peer.YawDegrees ?? 0,
                        peer.LastSeenAt)
                    : null;
            })
            .Where(marker => marker is not null)
            .Cast<NativePeerMarker>()
            .ToArray();
    }

    public NativeLanSyncState BuildUiState(MapPrototypeModel currentMap, bool hasLocalCoordinate)
    {
        TrimExpiredPeers();
        UpdateConnectedStatus(hasLocalCoordinate);
        var localEndpoint = $"{GetPreferredLocalAddress()}:{DefaultPort}";
        var remoteRoleText = mode == "host" ? "队友" : "房主";
        var remotePeerItems = ActivePeers
            .Where(peer => !string.Equals(peer.InstanceId, localState?.InstanceId, StringComparison.Ordinal))
            .Select(peer =>
            {
                var isCurrentMap = string.Equals(peer.MapId, currentMap.Id, StringComparison.OrdinalIgnoreCase);
                var hasPosition = peer.X is not null && peer.Z is not null;
                return new NativePeerListItem(
                    peer.DisplayName,
                    remoteRoleText,
                    NormalizeColor(peer.Color),
                    string.IsNullOrWhiteSpace(peer.MapName) ? "Online" : peer.MapName,
                    FormatLastSeen(peer.LastSeenAt),
                    isCurrentMap && hasPosition,
                    hasPosition);
            })
            .ToArray();
        var peerItems = BuildLocalPeerListItem(currentMap, localState)
            .Concat(remotePeerItems)
            .ToArray();
        return new NativeLanSyncState(
            mode,
            status,
            IsRunning,
            localEndpoint,
            remoteEndpoint,
            lastError,
            BuildConnectionHint(mode, IsRunning, hasLocalCoordinate, localEndpoint),
            peerItems);
    }

    private NativePeerListItem[] BuildLocalPeerListItem(MapPrototypeModel currentMap, PeerState? state)
    {
        if (!IsRunning || state is null)
        {
            return [];
        }

        var isCurrentMap = string.Equals(state.MapId, currentMap.Id, StringComparison.OrdinalIgnoreCase);
        var hasPosition = state.X is not null && state.Z is not null;
        var roleText = mode == "host" ? "房主（我）" : "队友（我）";
        return
        [
            new NativePeerListItem(
                state.DisplayName,
                roleText,
                NormalizeColor(state.Color),
                string.IsNullOrWhiteSpace(state.MapName) ? currentMap.Name : state.MapName,
                "本机",
                isCurrentMap && hasPosition,
                hasPosition)
        ];
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && listener is not null)
            {
                var client = await listener.AcceptTcpClientAsync(token);
                AddConnection(client, token);
                UpdateConnectedStatus();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            status = $"房间异常停止: {ex.Message}";
            lastError = ex.Message;
            cancellation = null;
            mode = "off";
        }
    }

    private async Task ConnectLoopAsync(string host, int port, CancellationToken token)
    {
        try
        {
            var client = new TcpClient();
            await client.ConnectAsync(host, port, token);
            AddConnection(client, token);
            status = "已连接到队友";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            status = $"连接失败: {ex.Message}";
            lastError = ex.Message;
            cancellation = null;
            mode = "off";
        }
    }

    private void AddConnection(TcpClient client, CancellationToken token)
    {
        var connection = new PeerConnection(Guid.NewGuid().ToString("N"), client);
        connections[connection.Id] = connection;
        _ = ReceiveLoopAsync(connection, token);
    }

    private async Task ReceiveLoopAsync(PeerConnection connection, CancellationToken token)
    {
        try
        {
            using var reader = new StreamReader(connection.Client.GetStream(), Encoding.UTF8, leaveOpen: true);
            while (!token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(token);
                if (line is null)
                {
                    break;
                }

                var peer = JsonSerializer.Deserialize(line, LanPeerSyncJsonContext.Default.PeerState);
                if (peer is null || string.Equals(peer.InstanceId, instanceId, StringComparison.Ordinal))
                {
                    continue;
                }

                peers[peer.InstanceId] = peer with { LastSeenAt = DateTimeOffset.UtcNow };
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
        finally
        {
            connections.TryRemove(connection.Id, out _);
            connection.Dispose();
            UpdateConnectedStatus();
        }
    }

    private void TrimExpiredPeers()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var peer in peers)
        {
            if (now - peer.Value.LastSeenAt > PeerTtl)
            {
                peers.TryRemove(peer.Key, out _);
            }
        }
    }

    private void UpdateConnectedStatus(bool hasLocalCoordinate = true)
    {
        if (!IsRunning)
        {
            return;
        }

        var coordinateText = hasLocalCoordinate ? "" : "，等待坐标，开启截图监听后会发送位置";
        status = mode switch
        {
            "host" => connections.IsEmpty
                ? $"房间已开启，等待队友加入{coordinateText}"
                : $"已连接 {connections.Count} 个队友{coordinateText}",
            "join" => connections.IsEmpty
                ? status
                : $"已连接到队友{coordinateText}",
            _ => status
        };
    }

    private static string BuildConnectionHint(string mode, bool isRunning, bool hasLocalCoordinate, string localEndpoint)
    {
        if (!isRunning)
        {
            return "房主开启房间后，在樱花穿透里把本机端口 39247 转发出去，再把外网地址发给队友。";
        }

        if (!hasLocalCoordinate)
        {
            return "同步已开启。开启截图监听或手动刷新坐标后，会把你的位置发给队友。";
        }

        return mode == "host"
            ? "房间已开启。把樱花穿透生成的外网地址发给队友，Windows 可能会弹出防火墙确认。"
            : "已加入队友房间，位置会随截图坐标刷新。";
    }

    private static (string Host, int Port) ParseEndpoint(string endpoint)
    {
        var text = endpoint.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return ("", 0);
        }

        var index = text.LastIndexOf(':');
        if (index <= 0 || index == text.Length - 1)
        {
            return ("", 0);
        }

        return int.TryParse(text[(index + 1)..], out var port) && port is >= 1 and <= 65535
            ? (text[..index], port)
            : ("", 0);
    }

    private static string FormatLastSeen(DateTimeOffset timestamp)
    {
        var seconds = Math.Max(0, (DateTimeOffset.UtcNow - timestamp).TotalSeconds);
        return seconds < 1 ? "just now" : $"{seconds:0}s ago";
    }

    private static string NormalizeColor(string color)
    {
        var text = color.Trim();
        return text.Length == 7 && text[0] == '#' ? text : "#4fd1ff";
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
            .Where(item => !IPAddress.IsLoopback(IPAddress.Parse(item)))
            .ToArray();

        if (physicalAddresses.Length > 0)
        {
            return physicalAddresses.FirstOrDefault(IsPrivateAddress)
                ?? physicalAddresses.First();
        }

        var fallbackAddresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up)
            .SelectMany(item => item.GetIPProperties().UnicastAddresses)
            .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(item => item.Address.ToString())
            .Where(item => !IPAddress.IsLoopback(IPAddress.Parse(item)))
            .ToArray();

        return fallbackAddresses.FirstOrDefault(IsPrivateAddress)
            ?? fallbackAddresses.FirstOrDefault()
            ?? "127.0.0.1";
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
        if (parts.Length != 4 || !int.TryParse(parts[0], out var first) || !int.TryParse(parts[1], out var second))
        {
            return false;
        }

        return first == 10 ||
            first == 192 && second == 168 ||
            first == 172 && second is >= 16 and <= 31;
    }

    private sealed class PeerConnection(string id, TcpClient client) : IDisposable
    {
        private readonly SemaphoreSlim sendLock = new(1, 1);

        public string Id { get; } = id;

        public TcpClient Client { get; } = client;

        public async Task SendAsync(byte[] payload)
        {
            await sendLock.WaitAsync();
            try
            {
                if (!Client.Connected)
                {
                    return;
                }

                await Client.GetStream().WriteAsync(payload);
            }
            catch
            {
            }
            finally
            {
                sendLock.Release();
            }
        }

        public void Dispose()
        {
            sendLock.Dispose();
            try
            {
                Client.Dispose();
            }
            catch
            {
            }
        }
    }
}

public sealed record PeerState(
    int SchemaVersion,
    string MessageType,
    string InstanceId,
    string DisplayName,
    string Color,
    string MapId,
    string MapName,
    double? X,
    double? Y,
    double? Z,
    double? YawDegrees,
    DateTimeOffset LastSeenAt)
{
    public static PeerState FromLocal(
        string instanceId,
        NativeLanSyncRequest request,
        MapPrototypeModel map,
        ScreenshotCoordinate? coordinate)
    {
        return new PeerState(
            1,
            "peerState",
            instanceId,
            string.IsNullOrWhiteSpace(request.DisplayName) ? "Local Player" : request.DisplayName.Trim(),
            string.IsNullOrWhiteSpace(request.Color) ? "#4fd1ff" : request.Color,
            map.Id,
            map.Name,
            coordinate?.X,
            coordinate?.Y,
            coordinate?.Z,
            coordinate?.YawDegrees,
            DateTimeOffset.UtcNow);
    }
}

[JsonSerializable(typeof(PeerState))]
internal sealed partial class LanPeerSyncJsonContext : JsonSerializerContext;
