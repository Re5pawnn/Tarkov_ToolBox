using TarkovMapLocatorDesktop.Models;

namespace TarkovMapLocatorDesktop.Services;

public static class AirdropTriangulationService
{
    public const double MinimumBaselineMeters = 10;
    public const double MinimumCrossingAngleDegrees = 5;
    private const double DirectionEpsilon = 1e-10;

    public static bool TryEstimate(
        AirdropBearingSample first,
        AirdropBearingSample second,
        out AirdropEstimate? estimate,
        out string failureReason)
    {
        estimate = null;
        failureReason = "";
        if (!string.Equals(first.MapId, second.MapId, StringComparison.OrdinalIgnoreCase))
        {
            failureReason = "两次截图不属于同一张地图";
            return false;
        }

        if (!AreFinite(first.X, first.Z, first.YawDegrees, second.X, second.Z, second.YawDegrees))
        {
            failureReason = "截图坐标或朝向无效";
            return false;
        }

        var deltaX = second.X - first.X;
        var deltaZ = second.Z - first.Z;
        var baseline = Math.Sqrt(deltaX * deltaX + deltaZ * deltaZ);
        if (baseline < MinimumBaselineMeters)
        {
            failureReason = $"A、B 两点只相距 {baseline:0.0} 米，请至少移动 {MinimumBaselineMeters:0} 米";
            return false;
        }

        var firstDirection = Direction(first.YawDegrees);
        var secondDirection = Direction(second.YawDegrees);
        var denominator = Cross(firstDirection.X, firstDirection.Z, secondDirection.X, secondDirection.Z);
        var dot = firstDirection.X * secondDirection.X + firstDirection.Z * secondDirection.Z;
        var crossingAngle = Math.Acos(Math.Clamp(Math.Abs(dot), 0, 1)) * 180d / Math.PI;
        if (crossingAngle < MinimumCrossingAngleDegrees || Math.Abs(denominator) < DirectionEpsilon)
        {
            failureReason = $"两条视线夹角只有 {crossingAngle:0.0}°，请横向移动后重新截取 B 点";
            return false;
        }

        var distanceFromA = Cross(deltaX, deltaZ, secondDirection.X, secondDirection.Z) / denominator;
        var distanceFromB = Cross(deltaX, deltaZ, firstDirection.X, firstDirection.Z) / denominator;
        if (distanceFromA < 0 || distanceFromB < 0)
        {
            failureReason = "两条射线只会在玩家身后相交，请重新瞄准空投截取 B 点";
            return false;
        }

        var estimateX = first.X + firstDirection.X * distanceFromA;
        var estimateZ = first.Z + firstDirection.Z * distanceFromA;
        if (!AreFinite(estimateX, estimateZ, distanceFromA, distanceFromB))
        {
            failureReason = "射线交点计算失败";
            return false;
        }

        estimate = new AirdropEstimate(
            estimateX,
            estimateZ,
            distanceFromA,
            distanceFromB,
            baseline,
            crossingAngle);
        return true;
    }

    public static bool TryBuildMapRay(
        MapCoordinateBounds bounds,
        AirdropBearingSample bearing,
        out AirdropMapRay? ray)
    {
        ray = null;
        if (!bounds.TryProject(bearing.X, bearing.Z, out var startX, out var startY)) return false;

        var direction = Direction(bearing.YawDegrees);
        if (!bounds.TryProjectDirection(direction.X, direction.Z, out var directionX, out var directionY))
            return false;

        var exitDistance = double.PositiveInfinity;
        AddBoundaryDistance(startX, directionX, ref exitDistance);
        AddBoundaryDistance(startY, directionY, ref exitDistance);
        if (!double.IsFinite(exitDistance) || exitDistance <= 0) return false;

        ray = new AirdropMapRay(
            startX,
            startY,
            Math.Clamp(startX + directionX * exitDistance, 0, 1),
            Math.Clamp(startY + directionY * exitDistance, 0, 1));
        return true;
    }

    private static (double X, double Z) Direction(double yawDegrees)
    {
        var radians = yawDegrees * Math.PI / 180d;
        return (Math.Sin(radians), Math.Cos(radians));
    }

    private static void AddBoundaryDistance(double start, double direction, ref double current)
    {
        if (Math.Abs(direction) < DirectionEpsilon) return;
        var distance = ((direction > 0 ? 1d : 0d) - start) / direction;
        if (distance > DirectionEpsilon && distance < current) current = distance;
    }

    private static double Cross(double ax, double az, double bx, double bz) => ax * bz - az * bx;

    private static bool AreFinite(params double[] values) => values.All(double.IsFinite);
}
