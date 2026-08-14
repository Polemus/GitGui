using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;

namespace Omnigit.Services;

/// <summary>
/// Stores access tokens. Kept separate from <see cref="RepositoryStore"/> on purpose:
/// a token can read and write all of the user's source code, so it must never sit in
/// the same plain JSON as ordinary settings.
/// </summary>
public interface ICredentialStore
{
    /// <summary>Human-readable backend name, shown so the user knows where tokens live.</summary>
    string Description { get; }

    /// <summary>False for the plain-file fallback, so the UI can warn.</summary>
    bool IsSecure { get; }

    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string secret);
    Task DeleteAsync(string key);
}

/// <summary>Picks the best credential backend available on this machine.</summary>
public static class CredentialStoreFactory
{
    public static ICredentialStore Create()
    {
        if (OperatingSystem.IsWindows())
            return new DpapiCredentialStore();

        if (OperatingSystem.IsMacOS() && CommandExists("security"))
            return new MacKeychainCredentialStore();

        if (OperatingSystem.IsLinux() && CommandExists("secret-tool"))
            return new SecretToolCredentialStore();

        // Better than refusing to work, but the user is told.
        return new FileCredentialStore();
    }

    private static bool CommandExists(string command)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("which", command)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            process!.WaitForExit(3000);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

/// <summary>Shared helpers for the backends that shell out to a CLI.</summary>
internal static class ProcessRunner
{
    public static async Task<(int ExitCode, string Output)> RunAsync(
        string file, string[] arguments, string? stdin = null)
    {
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info)
                            ?? throw new InvalidOperationException($"Could not start {file}.");

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
        }

        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, output);
    }
}

/// <summary>Linux: libsecret, which is GNOME Keyring or KWallet behind the scenes.</summary>
public sealed class SecretToolCredentialStore : ICredentialStore
{
    public string Description => "system keyring (libsecret)";
    public bool IsSecure => true;

    public async Task<string?> GetAsync(string key)
    {
        var (exitCode, output) = await ProcessRunner.RunAsync(
            "secret-tool", ["lookup", "service", "omnigit", "account", key]);

        return exitCode == 0 && output.Length > 0 ? output.TrimEnd('\n') : null;
    }

    public async Task SetAsync(string key, string secret)
    {
        // secret-tool reads the secret from stdin, so it never appears in the
        // process list the way a command-line argument would.
        var (exitCode, _) = await ProcessRunner.RunAsync(
            "secret-tool",
            ["store", "--label", $"Omnigit ({key})", "service", "omnigit", "account", key],
            stdin: secret);

        if (exitCode != 0)
            throw new InvalidOperationException("Could not save the token to the system keyring.");
    }

    public async Task DeleteAsync(string key)
        => await ProcessRunner.RunAsync("secret-tool", ["clear", "service", "omnigit", "account", key]);
}

/// <summary>macOS: the login keychain.</summary>
public sealed class MacKeychainCredentialStore : ICredentialStore
{
    public string Description => "macOS Keychain";
    public bool IsSecure => true;

    public async Task<string?> GetAsync(string key)
    {
        var (exitCode, output) = await ProcessRunner.RunAsync(
            "security", ["find-generic-password", "-a", key, "-s", "Omnigit", "-w"]);

        return exitCode == 0 ? output.TrimEnd('\n') : null;
    }

    public async Task SetAsync(string key, string secret)
    {
        // -U updates in place if the item already exists.
        // Note: `security` takes the secret as an argument, so it is briefly visible
        // in this user's own process list. There is no stdin form for this command.
        var (exitCode, _) = await ProcessRunner.RunAsync(
            "security", ["add-generic-password", "-a", key, "-s", "Omnigit", "-w", secret, "-U"]);

        if (exitCode != 0)
            throw new InvalidOperationException("Could not save the token to the Keychain.");
    }

    public async Task DeleteAsync(string key)
        => await ProcessRunner.RunAsync("security", ["delete-generic-password", "-a", key, "-s", "Omnigit"]);
}

/// <summary>
/// Windows: DPAPI, which encrypts with a key derived from the user's login. Another
/// user on the machine cannot decrypt it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiCredentialStore : ICredentialStore
{
    private readonly string _directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Omnigit", "credentials");

    public string Description => "Windows DPAPI";
    public bool IsSecure => true;

    public Task<string?> GetAsync(string key)
    {
        var file = PathFor(key);
        if (!File.Exists(file))
            return Task.FromResult<string?>(null);

        try
        {
            var protectedBytes = File.ReadAllBytes(file);
            var bytes = System.Security.Cryptography.ProtectedData.Unprotect(
                protectedBytes, optionalEntropy: null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser);

            return Task.FromResult<string?>(Encoding.UTF8.GetString(bytes));
        }
        catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException)
        {
            return Task.FromResult<string?>(null);
        }
    }

    public Task SetAsync(string key, string secret)
    {
        Directory.CreateDirectory(_directory);

        var bytes = System.Security.Cryptography.ProtectedData.Protect(
            Encoding.UTF8.GetBytes(secret), optionalEntropy: null,
            System.Security.Cryptography.DataProtectionScope.CurrentUser);

        File.WriteAllBytes(PathFor(key), bytes);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key)
    {
        var file = PathFor(key);
        if (File.Exists(file))
            File.Delete(file);

        return Task.CompletedTask;
    }

    private string PathFor(string key) => Path.Combine(_directory, FileCredentialStore.Sanitise(key));
}

/// <summary>
/// Last resort when no keyring is available. The file is created with owner-only
/// permissions, but the token is stored as plain text - anything running as this user
/// can read it. <see cref="IsSecure"/> is false so the UI can say so.
/// </summary>
public sealed class FileCredentialStore : ICredentialStore
{
    private readonly string _directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Omnigit", "credentials");

    public string Description => "a local file (no system keyring found)";
    public bool IsSecure => false;

    public async Task<string?> GetAsync(string key)
    {
        var file = Path.Combine(_directory, Sanitise(key));

        try
        {
            return File.Exists(file) ? await File.ReadAllTextAsync(file) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task SetAsync(string key, string secret)
    {
        Directory.CreateDirectory(_directory);
        var file = Path.Combine(_directory, Sanitise(key));

        await File.WriteAllTextAsync(file, secret);
        Restrict(file);
    }

    public Task DeleteAsync(string key)
    {
        var file = Path.Combine(_directory, Sanitise(key));
        if (File.Exists(file))
            File.Delete(file);

        return Task.CompletedTask;
    }

    /// <summary>Turns an account key into something safe to use as a file name.</summary>
    internal static string Sanitise(string key)
    {
        var builder = new StringBuilder(key.Length);

        foreach (var c in key)
            builder.Append(char.IsLetterOrDigit(c) ? c : '_');

        return builder.ToString();
    }

    private static void Restrict(string file)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            // Nothing else to try; IsSecure already reports this backend as weak.
        }
    }
}
