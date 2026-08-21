using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Omnigit.Services;

/// <summary>One file attached to a release.</summary>
public sealed record ReleaseAsset(string Name, Uri Url, long Size);

/// <summary>A published release, as much of it as the updater cares about.</summary>
public sealed record ReleaseInfo(
    Version Version,
    string Tag,
    string Notes,
    Uri Page,
    IReadOnlyList<ReleaseAsset> Assets);

public enum UpdateCheckOutcome
{
    /// <summary>This is the newest release, or newer than it.</summary>
    UpToDate,

    /// <summary>There is a newer one. <see cref="UpdateCheckResult.Release"/> describes it.</summary>
    UpdateAvailable,

    /// <summary>The check did not happen. Offline, rate-limited, no release yet.</summary>
    Failed,
}

public readonly record struct UpdateCheckResult(
    UpdateCheckOutcome Outcome,
    ReleaseInfo? Release = null,
    string? Detail = null);

public enum UpdateApplyOutcome
{
    /// <summary>The new version is in place. Relaunch to be running it.</summary>
    Applied,

    /// <summary>Nothing here can update this kind of install; something else does.</summary>
    NotSupported,

    /// <summary>It was attempted and did not work. Nothing has been changed.</summary>
    Failed,
}

public readonly record struct UpdateApplyResult(
    UpdateApplyOutcome Outcome,
    string? Detail = null);

public interface IUpdateService
{
    /// <summary>How this copy was installed. Fixed for the life of the process.</summary>
    InstallLocation Location { get; }

    /// <summary>Asks the release feed what the newest version is.</summary>
    Task<UpdateCheckResult> CheckAsync(CancellationToken cancel = default);

    /// <summary>
    /// Downloads the release, checks it against the published hash, and puts it where
    /// the running copy is. Does not restart anything.
    /// </summary>
    Task<UpdateApplyResult> ApplyAsync(
        ReleaseInfo release,
        IProgress<double>? progress = null,
        CancellationToken cancel = default);

    /// <summary>
    /// Starts the updated copy. The caller shuts this one down afterwards - closing the
    /// app is the lifetime's business, not a service's.
    /// </summary>
    bool Relaunch();
}

/// <summary>
/// Finds out whether there is a newer Omnigit and, where the install has no package
/// manager behind it, installs it.
/// </summary>
/// <remarks>
/// Being unable to check is an ordinary condition - the app runs offline perfectly
/// well and the release feed is someone else's server - so everything here reports an
/// outcome rather than throwing, the same rule the sync operations follow.
///
/// The check is unauthenticated on purpose. The repository is public, the endpoint is
/// one GET, and GitHub allows sixty an hour per address to anonymous callers, which a
/// once-a-day check cannot come near. Asking for a token would mean an updater that
/// stops working the moment someone signs out.
/// </remarks>
public sealed partial class UpdateService : IUpdateService
{
    /// <summary>Where releases are published. Not a manifest field: this is Omnigit itself.</summary>
    private const string DefaultRepository = "Polemus/Omnigit";

    private const string DefaultApi = "https://api.github.com/";

    /// <summary>
    /// Point the updater at another release feed, for testing it against something other
    /// than the real one.
    /// </summary>
    /// <remarks>
    /// The whole path - check, download, verify, swap, relaunch - can only be watched
    /// end to end against a release that has a newer version and a SHA256SUMS beside it,
    /// and making one of those on github.com means publishing it to everybody. Same
    /// reasoning as GITGUI_GITHUB_CLIENT_ID: the shipped default is the right one, and
    /// the variable exists so it does not have to be the only one.
    ///
    /// build/fake-release.sh serves exactly what these expect.
    /// </remarks>
    public const string ApiVariable = "OMNIGIT_UPDATE_API";

    /// <summary>The <c>owner/name</c> whose releases are read. See <see cref="ApiVariable"/>.</summary>
    public const string RepositoryVariable = "OMNIGIT_UPDATE_REPOSITORY";

    /// <summary>
    /// The manifest the release workflow generates over every artifact. A download is
    /// only installed if its hash is in here, so an interrupted transfer or a swapped
    /// file is a refusal rather than a broken installation.
    /// </summary>
    private const string ChecksumAsset = "SHA256SUMS";

    private readonly HttpClient _http;
    private readonly Uri _api;
    private readonly string _repository;

    public UpdateService()
        : this(NewClient(), ConfiguredApi(), null, ConfiguredRepository())
    {
    }

    /// <summary>Internal so the tests can answer for GitHub with a stub handler.</summary>
    internal UpdateService(
        HttpClient http,
        Uri api,
        InstallLocation? location = null,
        string? repository = null)
    {
        _http = http;
        _api = api;
        _repository = repository ?? DefaultRepository;
        Location = location ?? InstallDetection.Detect();
    }

    /// <summary>
    /// The feed to read, which is GitHub's unless something says otherwise. A variable
    /// holding nonsense falls back rather than failing: an updater is not worth refusing
    /// to start over.
    /// </summary>
    private static Uri ConfiguredApi()
    {
        var configured = Environment.GetEnvironmentVariable(ApiVariable);

        return !string.IsNullOrWhiteSpace(configured)
               && Uri.TryCreate(configured, UriKind.Absolute, out var api)
            ? api
            : new Uri(DefaultApi);
    }

    private static string ConfiguredRepository()
    {
        var configured = Environment.GetEnvironmentVariable(RepositoryVariable);

        // owner/name and nothing else - the value is pasted straight into a URL path.
        return !string.IsNullOrWhiteSpace(configured) && configured.Count(c => c == '/') == 1
            ? configured.Trim()
            : DefaultRepository;
    }

    public InstallLocation Location { get; }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancel = default)
    {
        try
        {
            var url = new Uri(_api, $"repos/{_repository}/releases/latest");

            using var response = await _http.GetAsync(url, cancel).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new(UpdateCheckOutcome.Failed, Detail: $"GitHub answered {(int)response.StatusCode}.");

            await using var body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancel).ConfigureAwait(false);

            var release = ReadRelease(json.RootElement);
            if (release is null)
                return new(UpdateCheckOutcome.Failed, Detail: "The newest release has no version in its tag.");

            return AppVersion.IsNewerThanCurrent(release.Version)
                ? new(UpdateCheckOutcome.UpdateAvailable, release)
                : new(UpdateCheckOutcome.UpToDate, release);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return new(UpdateCheckOutcome.Failed, Detail: ex.Message);
        }
    }

    public async Task<UpdateApplyResult> ApplyAsync(
        ReleaseInfo release,
        IProgress<double>? progress = null,
        CancellationToken cancel = default)
    {
        if (!Location.CanSelfUpdate || Location.Target is not { } target)
            return new(UpdateApplyOutcome.NotSupported, Location.ManagedBy);

        var wanted = AssetNameFor(Location.Medium, release.Version);
        if (wanted is null)
            return new(UpdateApplyOutcome.NotSupported);

        if (release.Assets.FirstOrDefault(a => a.Name == wanted) is not { } asset)
        {
            return new(
                UpdateApplyOutcome.Failed,
                $"{release.Tag} has no {wanted}. It may still be uploading.");
        }

        try
        {
            var expected = await ChecksumOfAsync(release, wanted, cancel).ConfigureAwait(false);
            if (expected is null)
                return new(UpdateApplyOutcome.Failed, $"{release.Tag} publishes no hash for {wanted}.");

            // Every medium downloads and verifies the same way and differs only in what
            // it does with the file afterwards, which is why the download lands in a
            // staging path chosen by the installer rather than a common temp directory:
            // the AppImage needs its next to the target so the swap is a rename.
            return Location.Medium switch
            {
                InstallMedium.AppImage =>
                    await ReplaceAppImageAsync(target, asset, expected, progress, cancel)
                        .ConfigureAwait(false),

                InstallMedium.DebPackage or InstallMedium.RpmPackage =>
                    await InstallSystemPackageAsync(asset, expected, progress, cancel)
                        .ConfigureAwait(false),

                InstallMedium.WindowsInstaller =>
                    await RunWindowsInstallerAsync(target, asset, expected, progress, cancel)
                        .ConfigureAwait(false),

                InstallMedium.WindowsPortable or InstallMedium.LinuxTarball =>
                    await ReplaceDirectoryAsync(target, asset, expected, progress, cancel)
                        .ConfigureAwait(false),

                InstallMedium.MacAppBundle =>
                    await ReplaceAppBundleAsync(target, asset, expected, progress, cancel)
                        .ConfigureAwait(false),

                _ => new(UpdateApplyOutcome.NotSupported, Location.ManagedBy),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            return new(UpdateApplyOutcome.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Replaces the .AppImage the user launched with the one just downloaded.
    /// </summary>
    /// <remarks>
    /// The download lands in the same directory as its target so the swap is a
    /// <c>rename</c>, which is atomic - there is no moment at which the file is half a
    /// new Omnigit. Crossing a filesystem would turn it into a copy, and a copy
    /// interrupted at the wrong second leaves the user with nothing to run.
    ///
    /// Replacing the file the running process was launched from is safe here in a way
    /// it is not elsewhere: the AppImage runtime mounted itself at startup and holds
    /// the old inode open, so this process keeps reading the copy it began with while
    /// the name points at the new one.
    ///
    /// The path is kept, version in the filename and all. That name now understates
    /// what it holds, which is a real cost - but the desktop entry, any dock pin and
    /// any symlink the user made all name this path, and writing the new version
    /// beside it would break every one of them to fix a label.
    /// </remarks>
    private async Task<UpdateApplyResult> ReplaceAppImageAsync(
        string target,
        ReleaseAsset asset,
        string expected,
        IProgress<double>? progress,
        CancellationToken cancel)
    {
        var directory = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(directory))
            return new(UpdateApplyOutcome.Failed, $"cannot tell what directory {target} is in");

        var staged = Path.Combine(directory, $".{Path.GetFileName(target)}.update");

        try
        {
            var actual = await DownloadAsync(asset, staged, progress, cancel).ConfigureAwait(false);

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                return new(
                    UpdateApplyOutcome.Failed,
                    $"{asset.Name} did not match its published hash and was discarded.");
            }

            // An AppImage that is not executable is a file the desktop cannot open, and
            // nothing later in the process would report why. The guard is for the
            // analyser rather than for the runtime - an AppImage only exists on Linux,
            // which CanSelfUpdate already established.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(
                    staged,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            File.Move(staged, target, overwrite: true);
            return new(UpdateApplyOutcome.Applied, target);
        }
        finally
        {
            // A failed or cancelled download must not leave most of an Omnigit lying
            // next to the real one, where the next attempt would append to it.
            if (File.Exists(staged))
                Delete(staged);
        }
    }

    /// <summary>Downloads to <paramref name="path"/> and returns the file's SHA-256.</summary>
    private async Task<string> DownloadAsync(
        ReleaseAsset asset,
        string path,
        IProgress<double>? progress,
        CancellationToken cancel)
    {
        using var response = await _http
            .GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancel)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        // The asset listing carries the size, and the response carries it again; the
        // listing is the one that is always there, since a redirected download can
        // arrive chunked.
        var total = response.Content.Headers.ContentLength ?? asset.Size;

        await using var incoming = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        await using var file = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);

        // Hashing as it arrives rather than reading the file back afterwards, which for
        // an eighty-megabyte download is a second pass over the disk for no reason.
        using var digest = SHA256.Create();

        var buffer = new byte[81920];
        long copied = 0;
        int read;

        while ((read = await incoming.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
        {
            digest.TransformBlock(buffer, 0, read, null, 0);
            await file.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);

            copied += read;
            if (total > 0)
                progress?.Report(Math.Min(1d, (double)copied / total));
        }

        digest.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(digest.Hash ?? []).ToLowerInvariant();
    }

    /// <summary>The published SHA-256 for one asset, or null if it isn't listed.</summary>
    private async Task<string?> ChecksumOfAsync(ReleaseInfo release, string name, CancellationToken cancel)
    {
        if (release.Assets.FirstOrDefault(a => a.Name == ChecksumAsset) is not { } manifest)
            return null;

        var text = await _http.GetStringAsync(manifest.Url, cancel).ConfigureAwait(false);
        return ParseChecksums(text).GetValueOrDefault(name);
    }

    /// <summary>
    /// Reads a <c>sha256sum</c> manifest: one hash, whitespace, then the file name.
    /// </summary>
    /// <remarks>
    /// The name half can carry a leading <c>*</c>, which sha256sum writes for a file it
    /// read in binary mode.
    ///
    /// A line is only a checksum if the first field is sixty-four hex digits. Splitting
    /// on whitespace alone accepts any prose with a space in it, which would put an
    /// entry in the table whose "hash" no download can ever match - a refusal to install
    /// with nothing to explain it. Internal so the parse is testable without a release.
    /// </remarks>
    internal static Dictionary<string, string> ParseChecksums(string text)
    {
        var checksums = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !IsSha256(parts[0]))
                continue;

            var name = parts[1].TrimStart('*').Trim();
            if (name.Length > 0)
                checksums[name] = parts[0].ToLowerInvariant();
        }

        return checksums;
    }

    private static bool IsSha256(string field) =>
        field.Length == 64 && field.All(char.IsAsciiHexDigit);

    /// <summary>
    /// The asset a given install would replace itself with, named exactly as
    /// build/expected-artifacts.sh names it.
    /// </summary>
    /// <remarks>
    /// Internal, and the tests hold it against that script - a filename invented here
    /// that no release contains would show up as an updater that never finds anything,
    /// which is indistinguishable from being up to date.
    /// </remarks>
    internal static string? AssetNameFor(InstallMedium medium, Version version)
    {
        var v = version.ToString(3);

        // Four spellings of one architecture, because each packaging tool insists on
        // its own: the kernel's, Debian's, the RPM world's, and .NET's runtime id.
        var (kernel, deb, rpm, rid) = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => ("x86_64", "amd64", "x86_64", "x64"),
            Architecture.Arm64 => ("aarch64", "arm64", "aarch64", "arm64"),
            _ => (null, null, null, null),
        };

        if (kernel is null)
            return null;

        return medium switch
        {
            InstallMedium.AppImage => $"Omnigit-{v}-{kernel}.AppImage",
            InstallMedium.DebPackage => $"omnigit_{v}_{deb}.deb",
            InstallMedium.RpmPackage => $"omnigit-{v}-1.{rpm}.rpm",
            InstallMedium.LinuxTarball => $"Omnigit-{v}-linux-{rid}.tar.gz",
            InstallMedium.WindowsInstaller => $"Omnigit-{v}-win-{rid}-setup.exe",
            InstallMedium.WindowsPortable => $"Omnigit-{v}-win-{rid}.zip",

            // One .dmg per architecture, named for the .NET runtime id rather than
            // Apple's "arm64"/"Intel" wording, because that is what package.sh is given.
            InstallMedium.MacAppBundle => $"Omnigit-{v}-osx-{rid}.dmg",

            _ => null,
        };
    }

    public bool Relaunch()
    {
        // Two of the Windows paths restart Omnigit from outside this process, and have
        // to: both replace the directory this process is running from, which cannot be
        // done until it has exited. The installer does it from its own [Run] entry; the
        // portable build leaves a helper script waiting on this process id. Either way
        // there is nothing here left to start, and trying would race them.
        if (Location.Medium is InstallMedium.WindowsInstaller
            || (Location.Medium is InstallMedium.WindowsPortable && OperatingSystem.IsWindows()))
        {
            return true;
        }

        if (Location.Executable is not { } target)
            return false;

        // macOS wants the bundle opened rather than the executable inside it run, so
        // launchd registers the app properly instead of giving it a bare process.
        if (Location.Medium is InstallMedium.MacAppBundle)
            return Spawn("/usr/bin/open", ["-n", "-a", target]);

        try
        {
            CloseDescriptorsOnExec();

            // UseShellExecute false so the new process does not inherit this one's
            // console handles on Windows; on Linux it is the plain fork/exec either way.
            using var started = Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = false,
            });

            return started is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Marks every open descriptor above the standard three close-on-exec, so the
    /// process started next inherits none of them.
    /// </summary>
    /// <remarks>
    /// An AppImage mounts itself with FUSE and execs the app inside, handing down a
    /// descriptor open on the mount directory *without* O_CLOEXEC. Without this the
    /// relaunched AppImage inherits it, the old runtime cannot unmount, and a stuck
    /// process and a stale /tmp/.mount_Omnigi* survive every update.
    ///
    /// Setting the flag is safe even on descriptors still in use: it changes nothing
    /// about this process, only what a child is handed. Linux only, as /proc/self/fd is.
    /// </remarks>
    private static void CloseDescriptorsOnExec()
    {
        if (!OperatingSystem.IsLinux())
            return;

        try
        {
            foreach (var entry in Directory.GetFileSystemEntries("/proc/self/fd"))
            {
                // 0, 1 and 2 are the standard streams, which a child is meant to get.
                if (int.TryParse(Path.GetFileName(entry), out var descriptor) && descriptor > 2)
                    _ = fcntl(descriptor, F_SETFD, FD_CLOEXEC);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort. Failing here costs a lingering mount, not the update.
        }
    }

    private const int F_SETFD = 2;
    private const int FD_CLOEXEC = 1;

    // DllImport rather than LibraryImport: the source generator emits unsafe code, and
    // turning AllowUnsafeBlocks on for the whole app to save one marshalling stub of
    // three ints is a poor trade.
    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);

    /// <summary>
    /// Turns one release object from the API into a <see cref="ReleaseInfo"/>, or null
    /// when its tag holds no version.
    /// </summary>
    private static ReleaseInfo? ReadRelease(JsonElement release)
    {
        var tag = release.TryGetProperty("tag_name", out var tagName)
            ? tagName.GetString()
            : null;

        if (AppVersion.Parse(tag) is not { } version || tag is null)
            return null;

        var assets = new List<ReleaseAsset>();
        if (release.TryGetProperty("assets", out var listed) && listed.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in listed.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;

                if (name is null || url is null || !Uri.TryCreate(url, UriKind.Absolute, out var parsed))
                    continue;

                var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
                assets.Add(new ReleaseAsset(name, parsed, size));
            }
        }

        var page = release.TryGetProperty("html_url", out var html)
                   && Uri.TryCreate(html.GetString(), UriKind.Absolute, out var link)
            ? link
            : new Uri($"https://github.com/{DefaultRepository}/releases/tag/{tag}");

        var body = release.TryGetProperty("body", out var text) ? text.GetString() : null;

        return new ReleaseInfo(version, tag, Summarise(body), page, assets);
    }

    /// <summary>
    /// Keeps the part of a release body that says what changed, and drops the rest.
    /// </summary>
    /// <remarks>
    /// release.yml builds the body as the metainfo's notes for that version followed by
    /// the standing install and getting-started sections, each under a <c>##</c>
    /// heading. Only the first part is news; the rest describes how to download a file
    /// the reader already has, inside an app that is about to replace it for them.
    /// </remarks>
    internal static string Summarise(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return string.Empty;

        var kept = new List<string>();

        foreach (var line in body.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.StartsWith("##", StringComparison.Ordinal))
                break;

            kept.Add(line);
        }

        return string.Join("\n", kept).Trim();
    }

    private static HttpClient NewClient()
    {
        var http = new HttpClient
        {
            // Long, because this same client fetches an eighty-megabyte AppImage. The
            // check itself is one small GET and is cancelled by the caller, not by this.
            Timeout = TimeSpan.FromMinutes(30),
        };

        // GitHub rejects an API request with no User-Agent outright.
        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Omnigit", AppVersion.Display));
        http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        return http;
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort. A stale .update file wastes disk and nothing else - the next
            // attempt truncates it, since the download opens with FileMode.Create.
        }
    }
}
