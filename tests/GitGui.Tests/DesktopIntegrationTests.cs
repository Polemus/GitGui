using GitGui.Services;

namespace GitGui.Tests;

/// <summary>
/// Covers the rewrite that turns the packaged desktop entry into one that points at
/// an AppImage. The rest of DesktopIntegration is filesystem work behind an
/// APPIMAGE environment variable, so there is nothing to run here without one.
/// </summary>
public class DesktopIntegrationTests
{
    private const string Packaged = """
        # Named after the app id rather than the binary.
        [Desktop Entry]
        Type=Application
        Name=GitGui
        Exec=gitgui
        Icon=io.github.polemus.GitGui
        Terminal=false
        StartupWMClass=GitGui
        """;

    private static string[] Lines(string entry) =>
        entry.TrimEnd('\n').Split('\n');

    [Fact]
    public void Localise_points_exec_at_the_appimage()
    {
        var result = DesktopIntegration.Localise(Packaged, "/home/u/Apps/GitGui.AppImage");

        Assert.Contains("Exec=/home/u/Apps/GitGui.AppImage", Lines(result));
        Assert.DoesNotContain("Exec=gitgui", Lines(result));
    }

    [Fact]
    public void Localise_keeps_the_keys_the_shell_matches_on()
    {
        var result = Lines(DesktopIntegration.Localise(Packaged, "/opt/GitGui.AppImage"));

        // Icon= is what stops the cog; StartupWMClass is what ties the running
        // window to this entry in the first place.
        Assert.Contains("Icon=io.github.polemus.GitGui", result);
        Assert.Contains("StartupWMClass=GitGui", result);
        Assert.Contains("[Desktop Entry]", result);
    }

    [Fact]
    public void Localise_adds_tryexec_so_a_deleted_appimage_hides_the_entry()
    {
        var result = Lines(DesktopIntegration.Localise(Packaged, "/opt/GitGui.AppImage"));

        Assert.Contains("TryExec=/opt/GitGui.AppImage", result);
    }

    [Fact]
    public void Localise_replaces_rather_than_repeats_when_run_over_its_own_output()
    {
        var once = DesktopIntegration.Localise(Packaged, "/old/GitGui.AppImage");
        var twice = DesktopIntegration.Localise(once, "/new/GitGui.AppImage");

        Assert.Single(Lines(twice), l => l.StartsWith("Exec="));
        Assert.Single(Lines(twice), l => l.StartsWith("TryExec="));
        Assert.Contains("Exec=/new/GitGui.AppImage", Lines(twice));
    }

    [Fact]
    public void Localise_appends_inside_the_group_when_the_source_ends_blank()
    {
        var result = Lines(DesktopIntegration.Localise("[Desktop Entry]\nName=GitGui\n\n\n", "/o/G.AppImage"));

        // A key after a blank line is still in the group, but a key after a *second*
        // group header would not be - keep them adjacent to what they belong to.
        Assert.Equal(
            Array.IndexOf(result, "Name=GitGui") + 1,
            Array.IndexOf(result, "Exec=/o/G.AppImage"));
    }

    [Fact]
    public void Localise_drops_the_packaged_comments_and_keeps_the_group_first()
    {
        var result = Lines(DesktopIntegration.Localise(Packaged, "/o/G.AppImage"));

        // They describe Exec=gitgui, which is exactly what this rewrite undoes.
        Assert.DoesNotContain(result, l => l.Contains("Named after the app id"));

        // A stray "# Exec=..." must not be mistaken for the real key either way.
        Assert.Equal("[Desktop Entry]", result.First(l => !l.StartsWith('#') && l.Length > 0));
    }

    [Theory]
    [InlineData("/opt/GitGui.AppImage", "/opt/GitGui.AppImage")]
    [InlineData("/home/u/My Apps/GitGui.AppImage", "\"/home/u/My Apps/GitGui.AppImage\"")]
    [InlineData("/home/u/$HOME/G.AppImage", "\"/home/u/\\$HOME/G.AppImage\"")]
    [InlineData("/home/u/a\"b/G.AppImage", "\"/home/u/a\\\"b/G.AppImage\"")]
    public void QuoteExec_follows_the_desktop_entry_rules(string path, string expected)
    {
        Assert.Equal(expected, DesktopIntegration.QuoteExec(path));
    }
}
