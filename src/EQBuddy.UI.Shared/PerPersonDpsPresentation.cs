using EQBuddy.Core;

namespace EQBuddy.UI.Shared;

/// <summary>
/// Turns each person's own <see cref="SessionStats.PersonDps"/> into the rows the widget's
/// per-person DPS readout draws — one row per person, the user's own first and then each
/// current teammate, exactly the order <see cref="SessionStats.PerPersonDps"/> hands them
/// in. Framework-free and pure, so "never summed, hidden when solo" is unit-tested rather
/// than trusted to the WPF tick (the WPF layer has no test project, docs/TestPlan.md §5).
/// </summary>
public static class PerPersonDpsPresentation
{
    /// <summary>One <see cref="CardRow"/> per person, in the order given — or an EMPTY
    /// list with fewer than two people (solo, no teammate has ever been known), which the
    /// caller reads as "hide the panel entirely" (<c>EqCardRows.FillVisible</c>'s contract):
    /// solo must look exactly like it did before this readout existed. A departed teammate
    /// (<see cref="SessionStats.PersonDps.IsCurrent"/> false) still counts toward that
    /// two-or-more test and still draws its own row — the panel stays up as long as a row
    /// does, tagged "(left)" via <see cref="CardRow.Note"/> (a separate run, same convention
    /// as the loot list's "(Foraged)"/"(Merged)") rather than baked into the name text
    /// itself.</summary>
    public static IReadOnlyList<CardRow> Rows(IReadOnlyList<SessionStats.PersonDps> people)
    {
        if (people.Count < 2) return [];
        var rows = new List<CardRow>(people.Count);
        foreach (var p in people) rows.Add(new CardRow(p.Name, Line(p), Note: p.IsCurrent ? null : "left"));
        return rows;
    }

    /// <summary>"176 dps (now 240) · 1,234,567 dmg" — the "(now …)" segment only while a
    /// fight is actually live for THAT person (<see cref="StatsSnapshot.CurrentDps"/>'s own
    /// rule, applied per person rather than to a combined rate). Same shapes the widget
    /// already uses elsewhere: the bare dps format <c>CombatHeader</c> wears, the "N0 dmg"
    /// a damage total wears everywhere else in this app (never abbreviated — a compacted
    /// unit this app has never shown would be a new convention for one row).</summary>
    private static string Line(SessionStats.PersonDps p) => p.CurrentDps > 0
        ? $"{p.SessionDps:0} dps (now {p.CurrentDps:0}) · {p.DamageDealt:N0} dmg"
        : $"{p.SessionDps:0} dps · {p.DamageDealt:N0} dmg";
}
