using System.Linq;
using Omnigit.HostProviders;

namespace Omnigit.ViewModels;

/// <summary>One row in the hosts list: what the site is, and where its description came from.</summary>
public sealed class HostEntryViewModel(IHostProvider provider, bool isUserDefined)
{
    public string Id => provider.Id;

    public string DisplayName => provider.DisplayName;

    /// <summary>Only the user's own manifests can be edited or removed from the UI.</summary>
    public bool IsUserDefined { get; } = isUserDefined;

    public bool IsBuiltInCode { get; } = provider is GitHubProvider;

    public string SourceLabel => IsUserDefined
        ? "Added by you"
        : IsBuiltInCode
            ? "Built in (code)"
            : "Built in (manifest)";

    /// <summary>Browser sign-in can't be described by a manifest, so say so plainly.</summary>
    public string AuthLabel => provider.Capabilities.AuthMethods.Contains(AuthMethod.BrowserDeviceLogin)
        ? "Token or browser"
        : "Token";
}
