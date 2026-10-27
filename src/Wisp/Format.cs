using System;

namespace Wisp;

static class Format
{
    public static string Reset(DateTimeOffset? at)
    {
        if (at == null) return "—";
        var left = at.Value - DateTimeOffset.Now;
        if (left <= TimeSpan.Zero) return "now";
        if (left.TotalDays >= 1) return $"{(int)left.TotalDays}d {left.Hours}h";
        if (left.TotalHours >= 1) return $"{(int)left.TotalHours}h {left.Minutes:00}m";
        return $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}m";
    }

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{Math.Max(1, (int)Math.Ceiling(t.TotalMinutes))}m";

    public static string Ago(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h" : $"{(int)t.TotalMinutes}m";
}
