using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DiskJanitor.Services;

public static partial class Bytes
{
    static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(long bytes)
    {
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes} B"
            : value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + Units[unit];
    }

    [GeneratedRegex(@"^\s*([\d.]+)\s*([kKMGTP]?)i?B")]
    private static partial Regex DockerSizeRegex();

    /// <summary>Parses docker's decimal sizes such as "620MB", "1.2kB" or "2B (virtual 62.4MB)".</summary>
    public static long ParseDocker(string? text)
    {
        var m = DockerSizeRegex().Match(text ?? "");
        if (!m.Success || !double.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var n))
            return 0;
        var multiplier = m.Groups[2].Value.ToUpperInvariant() switch
        {
            "K" => 1e3, "M" => 1e6, "G" => 1e9, "T" => 1e12, "P" => 1e15, _ => 1,
        };
        return (long)(n * multiplier);
    }

    [GeneratedRegex(@"([\d.]+)\s*([BKMGTP])")]
    private static partial Regex BinarySizeRegex();

    /// <summary>Parses binary sizes such as journalctl's "3.0G" or "512.0K".</summary>
    public static long ParseBinary(string? text)
    {
        var m = BinarySizeRegex().Match(text ?? "");
        if (!m.Success || !double.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var n))
            return 0;
        var power = "BKMGTP".IndexOf(m.Groups[2].Value[0]);
        return (long)(n * Math.Pow(1024, power));
    }
}

public static class TimeText
{
    public static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalDays < 1) return "today";
        if (span.TotalDays < 2) return "yesterday";
        if (span.TotalDays < 31) return $"{(int)span.TotalDays} days ago";
        if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)} months ago";
        var years = (int)(span.TotalDays / 365);
        return years == 1 ? "1 year ago" : $"{years} years ago";
    }
}
