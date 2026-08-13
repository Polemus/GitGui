using System;
using System.Collections.Generic;

namespace GitGui.HostProviders;

/// <summary>How a provider can get a token for a site.</summary>
public enum AuthMethod
{
    /// <summary>The user creates a token on the site and pastes it in. Works everywhere.</summary>
    PersonalAccessToken,

    /// <summary>Show a short code, the user approves it in a browser, we poll until done.</summary>
    BrowserDeviceLogin,
}

/// <summary>
/// A signed-in identity on one site. The token is held here only while the app runs;
/// saving it goes through <see cref="Services.ICredentialStore"/> so it never lands in
/// a plain text file next to the repository list.
/// </summary>
public sealed class HostAccount
{
    public required string ProviderId { get; init; }
    public required Uri BaseUrl { get; init; }
    public required string Login { get; init; }
    public required string DisplayName { get; init; }
    public required string Token { get; init; }
    public string? AvatarUrl { get; init; }

    /// <summary>Stable key for the credential store and for spotting duplicate sign-ins.</summary>
    public string Key => $"{ProviderId}|{BaseUrl.Host}|{Login}";

    public string Handle => $"@{Login}";
}

/// <summary>A repository as the site describes it, before it has been cloned.</summary>
public sealed class RemoteRepository
{
    public required string Name { get; init; }
    public required string Owner { get; init; }
    public required string CloneUrl { get; init; }
    public required string DefaultBranch { get; init; }
    public bool IsPrivate { get; init; }
    public string? Description { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }

    public string FullName => string.IsNullOrEmpty(Owner) ? Name : $"{Owner}/{Name}";
}

/// <summary>
/// The pending half of a browser login. The UI shows <see cref="UserCode"/> and
/// <see cref="VerificationUri"/> while the provider waits for the user to approve.
/// </summary>
public sealed class DeviceLogin
{
    public required string DeviceCode { get; init; }
    public required string UserCode { get; init; }
    public required Uri VerificationUri { get; init; }

    /// <summary>Seconds the site asks us to wait between checks.</summary>
    public int IntervalSeconds { get; init; } = 5;

    public DateTimeOffset ExpiresAt { get; init; } = DateTimeOffset.Now.AddMinutes(15);
}

/// <summary>Username and password handed to libgit2 for an HTTPS remote.</summary>
public sealed record GitCredentials(string Username, string Password);

/// <summary>What a provider supports, so the UI can hide what it can't do.</summary>
public sealed class HostCapabilities
{
    public required IReadOnlyList<AuthMethod> AuthMethods { get; init; }
    public bool CanListRepositories { get; init; } = true;
    public bool SupportsHttpsCredentials { get; init; } = true;
}

/// <summary>Thrown for site/API failures so the UI can show something meaningful.</summary>
public sealed class HostProviderException(string message, Exception? inner = null)
    : Exception(message, inner);
