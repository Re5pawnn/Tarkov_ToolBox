namespace TarkovMapLocatorDesktop.Models;

public sealed record AirdropBearingSample(
    string MapId,
    string FileName,
    double X,
    double Z,
    double YawDegrees,
    DateTimeOffset CapturedAt,
    long SortOrder)
{
    public static AirdropBearingSample FromCoordinate(string mapId, LiveCoordinate coordinate) => new(
        mapId,
        coordinate.FileName,
        coordinate.X,
        coordinate.Z,
        coordinate.YawDegrees,
        coordinate.CapturedAt,
        coordinate.SortOrder);
}

public sealed record AirdropEstimate(
    double X,
    double Z,
    double DistanceFromA,
    double DistanceFromB,
    double BaselineDistance,
    double CrossingAngleDegrees);

public sealed record AirdropMapRay(double StartX, double StartY, double EndX, double EndY);

public sealed record AirdropMapOverlay(
    AirdropMapRay? FirstRay,
    AirdropMapRay? SecondRay,
    double? EstimateX,
    double? EstimateY);
