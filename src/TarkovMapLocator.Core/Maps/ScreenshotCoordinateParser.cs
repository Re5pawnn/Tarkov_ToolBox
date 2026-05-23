using System.Globalization;
using System.Text.RegularExpressions;

namespace TarkovMapLocator.Core.Maps;

public static class ScreenshotCoordinateParser
{
    private static readonly Regex ScreenshotNameRegex = new(
        @"^(?<date>\d{4}-\d{2}-\d{2})\[(?<hour>\d{2})-(?<minute>\d{2})\]_" +
        @"(?<x>-?\d+(?:\.\d+)?),\s*(?<y>-?\d+(?:\.\d+)?),\s*(?<z>-?\d+(?:\.\d+)?)_" +
        @"(?<qx>-?\d+(?:\.\d+)?),\s*(?<qy>-?\d+(?:\.\d+)?),\s*" +
        @"(?<qz>-?\d+(?:\.\d+)?),\s*(?<qw>-?\d+(?:\.\d+)?)_" +
        @"(?<scale>-?\d+(?:\.\d+)?)(?:\s*\((?<index>\d+)\))?\.png$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static ScreenshotCoordinate? ParseFileName(string fileName, long modifiedAt)
    {
        var match = ScreenshotNameRegex.Match(fileName);
        if (!match.Success)
        {
            return null;
        }

        try
        {
            var date = match.Groups["date"].Value;
            var hour = int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture);
            var minute = int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture);
            var dateOnly = DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var timestamp = new DateTimeOffset(
                dateOnly.Year,
                dateOnly.Month,
                dateOnly.Day,
                hour,
                minute,
                0,
                TimeZoneInfo.Local.GetUtcOffset(DateTime.Now));

            var indexGroup = match.Groups["index"];
            var index = indexGroup.Success ? int.Parse(indexGroup.Value, CultureInfo.InvariantCulture) : 0;
            var x = ReadDouble(match, "x");
            var y = ReadDouble(match, "y");
            var z = ReadDouble(match, "z");
            var qx = ReadDouble(match, "qx");
            var qy = ReadDouble(match, "qy");
            var qz = ReadDouble(match, "qz");
            var qw = ReadDouble(match, "qw");
            var order = timestamp.ToUnixTimeMilliseconds() * 1000 + index;

            return new ScreenshotCoordinate(
                fileName,
                x,
                y,
                z,
                qx,
                qy,
                qz,
                qw,
                QuaternionToYawDegrees(qx, qy, qz, qw),
                order,
                modifiedAt);
        }
        catch
        {
            return null;
        }
    }

    public static double QuaternionToYawDegrees(double qx, double qy, double qz, double qw)
    {
        var sinyCosp = 2 * (qw * qy + qx * qz);
        var cosyCosp = 1 - 2 * (qy * qy + qz * qz);
        var yaw = Math.Atan2(sinyCosp, cosyCosp) * 180 / Math.PI;
        return (yaw + 360) % 360;
    }

    private static double ReadDouble(Match match, string groupName)
    {
        return double.Parse(match.Groups[groupName].Value, CultureInfo.InvariantCulture);
    }
}
