using EQBuddy.Core;

namespace EQBuddy.UI.Shared;

public static class TeammateStatusPresentation
{
    public static string? Warning(TimeSpan? offset) =>
        offset is { } value && value.Duration() > ClockDriftEstimator.Threshold
            ? $"Teammate totals paused: the logs' clocks differ by {value.Duration().TotalMinutes:0.#} minutes. Check both computers' clock and time zone settings."
            : null;

    public static string ClockStatus(TimeSpan? offset) => offset is { } value
        ? $"Teammate clock offset: {value.TotalSeconds:+0;-0;0} seconds (your clock minus theirs)."
        : "Teammate clock offset: not determined yet; waiting for a kill recorded by both logs.";
}
