using Omnigit.Services;

namespace Omnigit.Tests;

/// <summary>
/// The one-time carry-over from the directory the app used before it was renamed.
/// Getting this wrong is silent: the app starts, looks healthy, and knows nothing.
/// </summary>
public class AppPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "omnigit-tests", Guid.NewGuid().ToString("n"));

    private string Former => Path.Combine(_root, "GitGui");
    private string Current => Path.Combine(_root, "Omnigit");

    public AppPathsTests()
    {
        Directory.CreateDirectory(Path.Combine(Former, "hosts"));
        File.WriteAllText(Path.Combine(Former, "accounts.json"), "[]");
        File.WriteAllText(Path.Combine(Former, "hosts", "forge.json"), "{}");
        Directory.CreateDirectory(Current);
    }

    [Fact]
    public void AnEmptyDirectoryTakesTheOldOneOver()
    {
        AppPaths.CarryOver(Former, Current);

        Assert.Equal("[]", File.ReadAllText(Path.Combine(Current, "accounts.json")));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(Current, "hosts", "forge.json")));

        // Copied, not moved: a build from before the rename still has its state.
        Assert.True(File.Exists(Path.Combine(Former, "accounts.json")));
    }

    [Fact]
    public void ADirectoryAlreadyInUseIsLeftAlone()
    {
        File.WriteAllText(Path.Combine(Current, "accounts.json"), "current");

        AppPaths.CarryOver(Former, Current);

        Assert.Equal("current", File.ReadAllText(Path.Combine(Current, "accounts.json")));
        Assert.False(Directory.Exists(Path.Combine(Current, "hosts")));
    }

    /// <summary>Signing everything out must not be undone by the next launch.</summary>
    [Fact]
    public void TheCarryOverHappensOnlyOnce()
    {
        AppPaths.CarryOver(Former, Current);

        File.Delete(Path.Combine(Current, "accounts.json"));
        Directory.Delete(Path.Combine(Current, "hosts"), recursive: true);

        AppPaths.CarryOver(Former, Current);

        Assert.False(File.Exists(Path.Combine(Current, "accounts.json")));
    }

    [Fact]
    public void NoOldDirectoryIsNotAFailure()
    {
        Directory.Delete(Former, recursive: true);

        AppPaths.CarryOver(Former, Current);

        Assert.Empty(Directory.EnumerateFileSystemEntries(Current));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
