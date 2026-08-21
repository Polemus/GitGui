using System;
using System.Reflection;

namespace Omnigit.Services;

/// <summary>
/// What version this copy of Omnigit is, and how to compare it with another.
/// </summary>
/// <remarks>
/// The number comes from <c>&lt;Version&gt;</c> in the csproj by way of
/// <see cref="AssemblyInformationalVersionAttribute"/>, which the SDK stamps as
/// <c>0.3.0+&lt;sha&gt;</c>. That is deliberately the same value the release
/// workflow refuses to build without - see the version job in release.yml - so the
/// number shown here, the number in the tag and the number in the artifact names
/// are one number or the build does not happen.
/// </remarks>
public static class AppVersion
{
    /// <summary>The three-part version, with any build metadata dropped.</summary>
    public static Version Current { get; }

    /// <summary>The version as a user should read it: <c>0.3.0</c>.</summary>
    public static string Display => Current.ToString(3);

    /// <summary>The commit it was built from, short, or null for a local build.</summary>
    public static string? Commit { get; }

    static AppVersion()
    {
        var informational = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Current = Parse(informational) ?? new Version(0, 0, 0);

        var plus = informational?.IndexOf('+') ?? -1;
        if (informational is not null && plus >= 0 && plus + 1 < informational.Length)
        {
            var sha = informational[(plus + 1)..];
            Commit = sha[..Math.Min(7, sha.Length)];
        }
    }

    /// <summary>
    /// Reads a version out of anything a release might call itself - <c>v0.3.0</c>,
    /// <c>0.3.0+abc1234</c>, <c>0.3.0-rc1</c> - or null if there is no number in it.
    /// </summary>
    /// <remarks>
    /// Always three parts, and that is the point rather than tidiness.
    /// <see cref="Version"/> stores an absent field as -1 and sorts it below zero, so
    /// a bare <c>Version.Parse("0.3.0")</c> compares as *older* than
    /// <c>Version.Parse("0.3.0.0")</c>. Two spellings of one release would then read
    /// as an update available, forever.
    /// </remarks>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var span = text.AsSpan().Trim();

        if (span.Length > 0 && (span[0] == 'v' || span[0] == 'V'))
            span = span[1..];

        // Build metadata (+sha) and pre-release tags (-rc1) are not part of the number.
        // Neither is ordered here: a pre-release is treated as its own release, which
        // is right for a project that does not publish them.
        var end = span.IndexOfAny('+', '-', ' ');
        if (end >= 0)
            span = span[..end];

        if (!Version.TryParse(span, out var version))
            return null;

        return new Version(
            Math.Max(version.Major, 0),
            Math.Max(version.Minor, 0),
            Math.Max(version.Build, 0));
    }

    /// <summary>True when <paramref name="candidate"/> is a release worth offering.</summary>
    public static bool IsNewerThanCurrent(Version? candidate) =>
        candidate is not null && candidate > Current;
}
