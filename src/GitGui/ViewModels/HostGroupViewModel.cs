using System.Collections.Generic;
using GitGui.Models;

namespace GitGui.ViewModels;

/// <summary>Repositories bucketed under the host they came from, for the repo picker.</summary>
public sealed class HostGroupViewModel
{
    public required GitHost Host { get; init; }
    public required IReadOnlyList<RepositoryInfo> Repositories { get; init; }

    public string Header => Host.Name;

    /// <summary>
    /// The kind is only worth printing when it isn't already the name: on github.com both
    /// are "GitHub" and the heading stutters, whereas "git.homelab.net" gains from being
    /// labelled Gitea.
    /// </summary>
    public string SubHeader
    {
        get
        {
            var count = Repositories.Count == 1
                ? "1 repository"
                : $"{Repositories.Count} repositories";

            return Host.KindLabel == Host.Name ? count : $"{Host.KindLabel} · {count}";
        }
    }
}
