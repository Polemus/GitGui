using System.Collections.Generic;
using GitGui.Models;

namespace GitGui.ViewModels;

/// <summary>Repositories bucketed under the host they came from, for the repo picker.</summary>
public sealed class HostGroupViewModel
{
    public required GitHost Host { get; init; }
    public required IReadOnlyList<RepositoryInfo> Repositories { get; init; }

    public string Header => Host.Name;

    public string SubHeader => Repositories.Count == 1
        ? $"{Host.KindLabel} · 1 repository"
        : $"{Host.KindLabel} · {Repositories.Count} repositories";
}
