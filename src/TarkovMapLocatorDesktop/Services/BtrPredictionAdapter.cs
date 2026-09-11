using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using TarkovMapLocatorDesktop.Models;

namespace TarkovMapLocatorDesktop.Services;

/// <summary>
/// Loads the original tool's BTR predictor as an optional plug-in. Keeping every
/// original type behind reflection is intentional: an incomplete Windows App SDK
/// runtime must never prevent the map application itself from starting.
/// </summary>
public sealed class BtrPredictionAdapter
{
    private const string WoodsMapId = "5704e3c2d2720bac5b8b4567";
    private const string StreetsMapId = "5714dc692459777137212e12";
    private const string ServiceTypeName = "TarkovMapLocator.App.Services.BtrPredictionService";
    private const string MapTypeName = "TarkovMapLocator.App.Models.MapPrototypeModel";

    private object? _service;
    private Type? _mapType;
    private MethodInfo? _getRaidDurationMethod;
    private MethodInfo? _buildMarkersMethod;
    private int _runtimeFailureLogged;

    public BtrPredictionAdapter()
    {
        TryInitialize();
    }

    public bool IsAvailable => _service is not null && _mapType is not null &&
                               _getRaidDurationMethod is not null && _buildMarkersMethod is not null;

    public string? UnavailableReason { get; private set; }

    public static bool SupportsMap(MapDefinition? map) => map?.Id is "woods" or "streets-of-tarkov";

    public IReadOnlyList<MapMarker> BuildMarkers(
        MapDefinition? map,
        DateTimeOffset? gameStartAt,
        DateTimeOffset now)
    {
        if (!IsAvailable || !SupportsMap(map) || map?.WorldBounds is not { } bounds || gameStartAt is not { } startedAt)
            return [];

        try
        {
            var originalMap = Activator.CreateInstance(_mapType!)
                              ?? throw new InvalidOperationException("无法创建原工具地图模型。");
            SetProperty(originalMap, "Id", map.Id == "woods" ? WoodsMapId : StreetsMapId);
            SetProperty(originalMap, "Key", map.Id);
            SetProperty(originalMap, "Name", map.Name);
            SetProperty(originalMap, "NameId", map.Id == "woods" ? "Woods" : "TarkovStreets");
            SetProperty(originalMap, "MinX", bounds.X0);
            SetProperty(originalMap, "MinZ", bounds.Z0);
            SetProperty(originalMap, "MaxX", bounds.X1);
            SetProperty(originalMap, "MaxZ", bounds.Z1);
            SetProperty(originalMap, "ReverseCoordinate", bounds.ReverseCoordinate);

            var raidDuration = Convert.ToDouble(
                _getRaidDurationMethod!.Invoke(null, [originalMap]),
                CultureInfo.InvariantCulture);
            var elapsed = (now - startedAt).TotalSeconds;
            if (!double.IsFinite(elapsed) || elapsed < 0 || elapsed >= raidDuration)
                return [];

            var remaining = raidDuration - elapsed;
            var originalMarkers = _buildMarkersMethod!.Invoke(_service, [originalMap, remaining]) as IEnumerable;
            if (originalMarkers is null) return [];

            var result = new List<MapMarker>();
            foreach (var marker in originalMarkers)
            {
                if (marker is null) continue;
                var routeName = ReadString(marker, "RouteName");
                var statusText = ReadString(marker, "StatusText");
                var detailText = ReadString(marker, "DetailText");
                var markerRemaining = ReadDouble(marker, "RaidRemainingSeconds");
                var x = ReadDouble(marker, "U");
                var y = ReadDouble(marker, "V");
                if (!double.IsFinite(x) || !double.IsFinite(y)) continue;

                result.Add(new MapMarker
                {
                    Type = "btr",
                    Label = $"BTR {routeName}",
                    ToolTipText = $"BTR {routeName} · {statusText} · {detailText} · 战局剩余 {FormatTime(markerRemaining)}",
                    X = x,
                    Y = y,
                    ShowLabel = true,
                    ColorHex = ReadString(marker, "Color")
                });
            }

            return result;
        }
        catch (Exception exception)
        {
            Disable(Unwrap(exception));
            return [];
        }
    }

    private void TryInitialize()
    {
        try
        {
            var assemblyPath = Path.Combine(AppContext.BaseDirectory, "TarkovMapLocator.dll");
            if (!File.Exists(assemblyPath))
                throw new FileNotFoundException("缺少原工具 BTR 预测程序集。", assemblyPath);

            var assembly = Assembly.LoadFrom(assemblyPath);
            var serviceType = assembly.GetType(ServiceTypeName, throwOnError: true)!;
            _mapType = assembly.GetType(MapTypeName, throwOnError: true)!;
            _getRaidDurationMethod = serviceType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .SingleOrDefault(method => method.Name == "GetRaidDurationSeconds" && method.GetParameters().Length == 1)
                ?? throw new MissingMethodException(ServiceTypeName, "GetRaidDurationSeconds");
            _buildMarkersMethod = serviceType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .SingleOrDefault(method => method.Name == "BuildMarkers" && method.GetParameters().Length == 2)
                ?? throw new MissingMethodException(ServiceTypeName, "BuildMarkers");
            _service = Activator.CreateInstance(serviceType)
                       ?? throw new InvalidOperationException("无法创建原工具 BTR 预测服务。");
            UnavailableReason = null;
        }
        catch (Exception exception)
        {
            Disable(Unwrap(exception));
        }
    }

    private void Disable(Exception exception)
    {
        _service = null;
        _mapType = null;
        _getRaidDurationMethod = null;
        _buildMarkersMethod = null;
        UnavailableReason = exception.Message;
        if (Interlocked.Exchange(ref _runtimeFailureLogged, 1) == 0)
            RuntimeLogService.Warning("BTR 预测", "原工具 BTR 组件不可用，地图其余功能继续运行", exception.ToString());
    }

    private static void SetProperty(object target, string name, object value)
    {
        var property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                       ?? throw new MissingMemberException(target.GetType().FullName, name);
        property.SetValue(target, value);
    }

    private static object? ReadProperty(object target, string name) =>
        target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);

    private static string ReadString(object target, string name) => ReadProperty(target, name)?.ToString() ?? "";

    private static double ReadDouble(object target, string name) =>
        Convert.ToDouble(ReadProperty(target, name), CultureInfo.InvariantCulture);

    private static Exception Unwrap(Exception exception) =>
        exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;

    private static string FormatTime(double seconds)
    {
        var value = Math.Max(0, (int)Math.Round(seconds));
        return $"{value / 60:00}:{value % 60:00}";
    }
}
