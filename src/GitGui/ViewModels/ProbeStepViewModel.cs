using GitGui.HostProviders;

namespace GitGui.ViewModels;

/// <summary>One line of a connection test result: a glyph, what was checked, what happened.</summary>
public sealed class ProbeStepViewModel(ProbeStep step)
{
    public string Name => step.Name;

    public string Detail => step.Detail;

    public bool IsPassed => step.Outcome == ProbeOutcome.Passed;

    public bool IsFailed => step.Outcome == ProbeOutcome.Failed;

    public string Glyph => step.Outcome switch
    {
        ProbeOutcome.Passed => "✓",
        ProbeOutcome.Failed => "✕",
        _ => "–",
    };
}
