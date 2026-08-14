using System;

namespace Omnigit.Models;

/// <summary>Short, git-client-flavoured relative timestamps ("12 minutes ago").</summary>
public static class TimeFormat
{
    public static string Relative(DateTimeOffset when)
    {
        var delta = DateTimeOffset.Now - when;

        if (delta < TimeSpan.FromMinutes(1))
            return "just now";
        if (delta < TimeSpan.FromHours(1))
            return Plural((int)delta.TotalMinutes, "minute");
        if (delta < TimeSpan.FromDays(1))
            return Plural((int)delta.TotalHours, "hour");
        if (delta < TimeSpan.FromDays(30))
            return Plural((int)delta.TotalDays, "day");
        if (delta < TimeSpan.FromDays(365))
            return Plural((int)(delta.TotalDays / 30), "month");

        return Plural((int)(delta.TotalDays / 365), "year");
    }

    private static string Plural(int n, string unit) =>
        n == 1 ? $"1 {unit} ago" : $"{n} {unit}s ago";
}
