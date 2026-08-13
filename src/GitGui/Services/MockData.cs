using System;
using System.Collections.Generic;
using GitGui.Models;

namespace GitGui.Services;

/// <summary>
/// Hard-coded sample content for the UI mockup. Every list here is what the real
/// data layer will eventually return from libgit2 plus the host APIs — the shape
/// is meant to be the contract, so swapping in real services touches only this file.
/// </summary>
public static class MockData
{
    public static GitHost GitHubDotCom { get; } = new()
    {
        Id = "github",
        Name = "GitHub",
        Kind = HostKind.GitHub,
        BaseUrl = "https://github.com",
        AccentHex = "#3399CC",
        Badge = "G",
    };

    public static GitHost HomelabGitea { get; } = new()
    {
        Id = "gitea-homelab",
        Name = "git.homelab.net",
        Kind = HostKind.Gitea,
        BaseUrl = "https://git.homelab.net",
        AccentHex = "#609926",
        Badge = "T",
    };

    public static IReadOnlyList<GitHost> Hosts { get; } = [GitHubDotCom, HomelabGitea];

    public static IReadOnlyList<Account> Accounts { get; } =
    [
        new Account
        {
            Login = "Polemus",
            DisplayName = "Dusty Roberts",
            Host = GitHubDotCom,
            Initials = "DR",
            AvatarHex = "#3399CC",
            AuthMethod = "OAuth device flow",
            RepositoryCount = 24,
        },
        new Account
        {
            Login = "stoic",
            DisplayName = "Homelab",
            Host = HomelabGitea,
            Initials = "HL",
            AvatarHex = "#609926",
            AuthMethod = "Personal access token",
            RepositoryCount = 7,
        },
    ];

    public static IReadOnlyList<RepositoryInfo> Repositories { get; } =
    [
        new RepositoryInfo
        {
            Name = "GitGui",
            Owner = "Polemus",
            Host = GitHubDotCom,
            LocalPath = "~/Code/GitGui",
            DefaultBranch = "main",
            IsPrivate = true,
            Ahead = 2,
            Behind = 0,
            LastFetched = DateTimeOffset.Now.AddMinutes(-12),
        },
        new RepositoryInfo
        {
            Name = "Fleet-Manager",
            Owner = "Polemus",
            Host = GitHubDotCom,
            LocalPath = "~/Code/Fleet-Manager",
            DefaultBranch = "main",
            IsPrivate = true,
            Ahead = 0,
            Behind = 3,
            LastFetched = DateTimeOffset.Now.AddHours(-2),
        },
        new RepositoryInfo
        {
            Name = "relocin-site",
            Owner = "Polemus",
            Host = GitHubDotCom,
            LocalPath = "~/Code/relocin-site",
            DefaultBranch = "main",
            IsPrivate = false,
            Ahead = 0,
            Behind = 0,
            LastFetched = DateTimeOffset.Now.AddDays(-1),
        },
        new RepositoryInfo
        {
            Name = "infra-notes",
            Owner = "stoic",
            Host = HomelabGitea,
            LocalPath = "~/Code/infra-notes",
            DefaultBranch = "trunk",
            IsPrivate = true,
            Ahead = 1,
            Behind = 0,
            LastFetched = DateTimeOffset.Now.AddMinutes(-40),
        },
        new RepositoryInfo
        {
            Name = "loco-telemetry",
            Owner = "stoic",
            Host = HomelabGitea,
            LocalPath = "~/Code/loco-telemetry",
            DefaultBranch = "main",
            IsPrivate = true,
            Ahead = 0,
            Behind = 0,
            LastFetched = DateTimeOffset.Now.AddDays(-3),
        },
    ];

    public static IReadOnlyList<BranchInfo> Branches { get; } =
    [
        new BranchInfo
        {
            Name = "main",
            LastCommitSummary = "Wire the accounts screen to the host registry",
            LastCommitAt = DateTimeOffset.Now.AddMinutes(-35),
            IsDefault = true,
        },
        new BranchInfo
        {
            Name = "feature/gitea-auth",
            LastCommitSummary = "Add PAT entry flow for self-hosted Gitea",
            LastCommitAt = DateTimeOffset.Now.AddHours(-5),
        },
        new BranchInfo
        {
            Name = "feature/diff-viewer",
            LastCommitSummary = "Split-view diff prototype",
            LastCommitAt = DateTimeOffset.Now.AddDays(-2),
        },
        new BranchInfo
        {
            Name = "fix/window-chrome",
            LastCommitSummary = "Respect system title bar on Linux",
            LastCommitAt = DateTimeOffset.Now.AddDays(-4),
        },
    ];

    // Lazy so it can reference WorkingChanges, which is declared further down.
    private static IReadOnlyList<CommitInfo>? _history;

    public static IReadOnlyList<CommitInfo> History => _history ??=
    [
        new CommitInfo
        {
            Sha = "a3f91c4e8b2d5f7a9c1e3b5d7f9a1c3e5b7d9f1a",
            Summary = "Wire the accounts screen to the host registry",
            AuthorName = "Dusty Roberts",
            AuthorInitials = "DR",
            AvatarHex = "#3399CC",
            CommittedAt = DateTimeOffset.Now.AddMinutes(-35),
            FilesChanged = 4,
        },
        new CommitInfo
        {
            Sha = "7d2e5a9f1b3c6e8a0d2f4b6c8e0a2d4f6b8c0e2a",
            Summary = "Add Gitea host kind alongside GitHub",
            AuthorName = "Dusty Roberts",
            AuthorInitials = "DR",
            AvatarHex = "#3399CC",
            CommittedAt = DateTimeOffset.Now.AddHours(-3),
            FilesChanged = 7,
        },
        new CommitInfo
        {
            Sha = "c8b1f4d7a2e5c8b1f4d7a2e5c8b1f4d7a2e5c8b1",
            Summary = "Extract diff rendering into its own view",
            AuthorName = "Dusty Roberts",
            AuthorInitials = "DR",
            AvatarHex = "#3399CC",
            CommittedAt = DateTimeOffset.Now.AddHours(-9),
            FilesChanged = 3,
        },
        new CommitInfo
        {
            Sha = "f5a8c2e6b9d3f7a1c5e9b3d7f1a5c9e3b7d1f5a8",
            Summary = "Release workflow: build deb, rpm, msi and dmg",
            AuthorName = "Dusty Roberts",
            AuthorInitials = "DR",
            AvatarHex = "#3399CC",
            CommittedAt = DateTimeOffset.Now.AddDays(-1),
            FilesChanged = 2,
        },
        new CommitInfo
        {
            Sha = "2b6d9a3f7c1e5b9d3a7f1c5e9b3d7a1f5c9e3b7d",
            Summary = "Dark theme tokens and diff gutter colours",
            AuthorName = "Dusty Roberts",
            AuthorInitials = "DR",
            AvatarHex = "#3399CC",
            CommittedAt = DateTimeOffset.Now.AddDays(-2),
            FilesChanged = 5,
        },
        new CommitInfo
        {
            Sha = "9e3c7b1d5f9a3c7e1b5d9f3a7c1e5b9d3f7a1c5e",
            Summary = "Scaffold Avalonia shell with FluentAvalonia",
            AuthorName = "Dusty Roberts",
            AuthorInitials = "DR",
            AvatarHex = "#3399CC",
            CommittedAt = DateTimeOffset.Now.AddDays(-3),
            FilesChanged = 12,
        },
        new CommitInfo
        {
            Sha = "4c8e2a6f0b4d8e2a6c0f4b8d2e6a0c4f8b2d6e0a",
            Summary = "Initial commit",
            AuthorName = "Dusty Roberts",
            AuthorInitials = "DR",
            AvatarHex = "#3399CC",
            CommittedAt = DateTimeOffset.Now.AddDays(-3).AddMinutes(-20),
            FilesChanged = 9,
        },
    ];

    public static IReadOnlyList<FileChange> WorkingChanges { get; } =
    [
        new FileChange
        {
            Path = "src/GitGui/ViewModels/MainWindowViewModel.cs",
            Status = ChangeStatus.Modified,
            Additions = 12,
            Deletions = 3,
            Diff =
            [
                Hunk("@@ -14,9 +14,18 @@ public partial class MainWindowViewModel : ViewModelBase"),
                Ctx(14, 14, "    [ObservableProperty]"),
                Ctx(15, 15, "    public partial RepositoryInfo? SelectedRepository { get; set; }"),
                Ctx(16, 16, ""),
                Rem(17, "    public MainWindowViewModel()"),
                Rem(18, "    {"),
                Rem(19, "        Repositories = new(MockData.Repositories);"),
                Add(17, "    [ObservableProperty]"),
                Add(18, "    public partial bool IsAccountsPageVisible { get; set; }"),
                Add(19, ""),
                Add(20, "    public MainWindowViewModel()"),
                Add(21, "    {"),
                Add(22, "        Repositories = new(MockData.Repositories);"),
                Add(23, "        Accounts = new(MockData.Accounts);"),
                Add(24, "        Hosts = new(MockData.Hosts);"),
                Ctx(20, 25, "        SelectedRepository = Repositories[0];"),
                Ctx(21, 26, "        Branches = new(MockData.Branches);"),
                Ctx(22, 27, "    }"),
            ],
        },
        new FileChange
        {
            Path = "src/GitGui/Views/AccountsView.axaml",
            Status = ChangeStatus.Added,
            Additions = 48,
            Deletions = 0,
            Diff =
            [
                Hunk("@@ -0,0 +1,48 @@"),
                Add(1, "<UserControl xmlns=\"https://github.com/avaloniaui\""),
                Add(2, "             xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\""),
                Add(3, "             x:Class=\"GitGui.Views.AccountsView\">"),
                Add(4, "  <StackPanel Spacing=\"16\" Margin=\"32\">"),
                Add(5, "    <TextBlock Classes=\"h1\" Text=\"Accounts\" />"),
                Add(6, "    <ItemsControl ItemsSource=\"{Binding Accounts}\" />"),
                Add(7, "  </StackPanel>"),
                Add(8, "</UserControl>"),
            ],
        },
        new FileChange
        {
            Path = ".github/workflows/release.yml",
            Status = ChangeStatus.Modified,
            Additions = 8,
            Deletions = 1,
            Diff =
            [
                Hunk("@@ -21,7 +21,14 @@ jobs:"),
                Ctx(21, 21, "    strategy:"),
                Ctx(22, 22, "      matrix:"),
                Ctx(23, 23, "        include:"),
                Rem(24, "          - { os: ubuntu-latest, rid: linux-x64 }"),
                Add(24, "          - { os: ubuntu-latest,  rid: linux-x64 }"),
                Add(25, "          - { os: ubuntu-latest,  rid: linux-arm64 }"),
                Add(26, "          - { os: windows-latest, rid: win-x64 }"),
                Add(27, "          - { os: macos-latest,   rid: osx-arm64 }"),
                Add(28, "          - { os: macos-13,       rid: osx-x64 }"),
                Ctx(25, 29, "    runs-on: ${{ matrix.os }}"),
                Ctx(26, 30, "    steps:"),
                Ctx(27, 31, "      - uses: actions/checkout@v4"),
            ],
        },
        new FileChange
        {
            Path = "README.md",
            Status = ChangeStatus.Modified,
            Additions = 4,
            Deletions = 2,
            Diff =
            [
                Hunk("@@ -3,8 +3,10 @@"),
                Ctx(3, 3, "A desktop git client for more than one forge."),
                Ctx(4, 4, ""),
                Rem(5, "Supports GitHub."),
                Rem(6, "Linux only for now."),
                Add(5, "Supports GitHub and Gitea, including self-hosted"),
                Add(6, "instances behind your own domain."),
                Add(7, ""),
                Add(8, "Ships for Linux, Windows and macOS."),
                Ctx(7, 9, ""),
                Ctx(8, 10, "## Building"),
            ],
        },
        new FileChange
        {
            Path = "src/GitGui/Assets/old-logo.svg",
            Status = ChangeStatus.Deleted,
            Additions = 0,
            Deletions = 6,
            Diff =
            [
                Hunk("@@ -1,6 +0,0 @@"),
                Rem(1, "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 64 64\">"),
                Rem(2, "  <circle cx=\"32\" cy=\"32\" r=\"28\" fill=\"#3b82f6\" />"),
                Rem(3, "  <path d=\"M20 32h24M32 20v24\" stroke=\"#fff\" stroke-width=\"4\" />"),
                Rem(4, "</svg>"),
                Rem(5, ""),
                Rem(6, "<!-- superseded by gitgui.svg -->"),
            ],
        },
    ];

    private static DiffLine Hunk(string text) =>
        new() { Kind = DiffLineKind.HunkHeader, Text = text };

    private static DiffLine Ctx(int oldNo, int newNo, string text) =>
        new()
        {
            Kind = DiffLineKind.Context,
            Text = text,
            OldNumber = oldNo.ToString(),
            NewNumber = newNo.ToString(),
        };

    private static DiffLine Add(int newNo, string text) =>
        new() { Kind = DiffLineKind.Added, Text = text, NewNumber = newNo.ToString() };

    private static DiffLine Rem(int oldNo, string text) =>
        new() { Kind = DiffLineKind.Removed, Text = text, OldNumber = oldNo.ToString() };
}
