using EQBuddy.Core;

namespace EQBuddy.UI.Shared;

/// <summary>
/// The Combat and Healing cards' summary blocks (Gate 5b).
///
/// These are the densest untested text in the app: roughly a dozen conditional fragments
/// each, composed inline in <c>RefreshUi</c> as one interpolated string, on the card a
/// player reads most. Several of the conditions encode real decisions that were argued
/// over — which DPS model is honest, when a partial window must say so, when a fizzle
/// count is redundant — and none of them could be asserted, because the WPF layer has no
/// test project (docs/TestPlan.md §5).
///
/// A list of lines rather than one newline-joined string, so a test can name the line it
/// means and an absent line is absent rather than blank.
/// </summary>
public static class CombatPresentation
{
    public static List<string> SummaryLines(StatsSnapshot s)
    {
        var swings = s.HitCount + s.MissCount;
        var accuracy = swings > 0 ? (double)s.HitCount / swings * 100 : 0;
        var critRate = s.HitCount > 0 ? (double)s.CritCount / s.HitCount * 100 : 0;
        var incoming = s.AvoidedIncoming + s.MeleeHitsTaken;
        var avoidance = incoming > 0 ? (double)s.AvoidedIncoming / incoming * 100 : 0;
        var combat = TimeSpan.FromSeconds(s.CombatSeconds);

        var lines = new List<string>
        {
            $"Dealt {s.DamageDealt:N0} ({s.MeleeDamage:N0} melee / {s.SpellDamage:N0} spell)",
            $"{s.CritCount} crits ({critRate:0.#}% rate) · {accuracy:0}% accuracy",
            $"In combat {(int)combat.TotalMinutes}m {combat.Seconds}s this session",
        };

        // BOTH dps models, each labelled (the Companion-parity ask): in-combat is the
        // honest camp number, because medding doesn't dilute it; wall-clock is what a raid
        // night actually produced. Neither one is "the" DPS, so the card never prints a
        // bare number and lets the reader assume which.
        if (s.SessionDps > 0 && s.SessionStart is { } start && s.LastEventTime is { } last)
            lines.Add($"Session dps: {s.SessionDps:0.#} in combat · " +
                      $"{s.DamageDealt / Math.Max(1, (last - start).TotalSeconds):0.#} wall-clock");

        // A window that hasn't filled yet is labelled, because a 3-minute average shown as
        // a 15-minute one is a lie in the direction of whatever just happened.
        if (s.Recent is { } recent)
            lines.Add($"Last {(int)recent.Window.TotalMinutes}m: {recent.Dps:0.#} dps"
                      + (recent.HasFullWindow ? "" : " (partial window)"));

        lines.Add($"Biggest hit: {s.MaxHit:N0} ({s.MaxHitDesc})");
        lines.Add($"Taken {s.DamageTaken:N0} · avoided {s.AvoidedIncoming} of {incoming} " +
                  $"melee attacks ({avoidance:0}%)");

        if (s.SpecialHits.Count > 0)
            lines.Add(string.Join(" · ", s.SpecialHits.Select(x => $"{DuoStats.DisplayActorTag(x.Name)} {x.Count}")));

        if (s.DotDamage + s.DirectSpellDamage > 0)
            lines.Add($"Your spells: {s.DotDamage:N0} over time / {s.DirectSpellDamage:N0} direct");

        if (CastingLine(s) is { } casting) lines.Add(casting);
        if (s.CurrentStance.Length > 0) lines.Add($"Stance: {s.CurrentStance}");
        return lines;
    }

    /// <summary>Casting outcomes, and the reason there are two shapes of this line.
    ///
    /// Cast completion SUBSUMES the fizzle count, so where the log carries cast lines the
    /// old fizzle/resist line would be saying the same thing twice. A log with no cast
    /// lines in it gets the old line instead — and neither appears when nothing went
    /// wrong, because "0 fizzled" is not news.
    ///
    /// Blocked is a completed cast a standing buff refused ("did not take hold") — a
    /// STACKING fact rather than a casting failure, so it joins the parenthetical only
    /// when it happened rather than sitting at zero beside the real failures.</summary>
    public static string? CastingLine(StatsSnapshot s)
    {
        if (s.CastCompletion is { } completion)
            return $"Casts {s.CastsStarted} · {completion * 100:0}% completed"
                   + $" ({s.CastsInterrupted} interrupted · {s.Fizzles} fizzled · {s.Resists} resisted"
                   + (s.Blocked > 0 ? $" · {s.Blocked} blocked" : "") + ")";
        if (s.Fizzles + s.Resists + s.Blocked > 0)
            return $"Fizzles {s.Fizzles} · resists {s.Resists}"
                   + (s.Blocked > 0 ? $" · blocked {s.Blocked}" : "");
        return null;
    }

    /// <summary>The Healing card's summary.</summary>
    /// <param name="regenLine">The regen/hymn estimate, which depends on a SETTING (the
    /// player's own hp-per-tick) and so is composed by the caller — this module is given
    /// the finished sentence rather than the settings object, which keeps it a pure
    /// function of the snapshot plus one string.</param>
    public static List<string> HealingLines(StatsSnapshot s, string? regenLine)
    {
        var lines = new List<string>
        {
            $"Done {s.HealingDone:N0} · received {s.HealingReceived:N0}",
        };
        if (s.Recent is { Hps: > 0 } recent)
            lines.Add($"Last {(int)recent.Window.TotalMinutes}m: {recent.Hps:0.#} hps");
        if (s.RegenTicks > 0 && regenLine is { Length: > 0 }) lines.Add(regenLine);
        if (s.RuneBlockCount > 0)
            lines.Add($"Rune absorbed {s.RuneBlockCount} hit{(s.RuneBlockCount == 1 ? "" : "s")}"
                      + $" (best streak {s.RuneBlockStreakMax}"
                      + (s.RuneBlockStreak > 0 ? $", current {s.RuneBlockStreak}" : "") + ")");
        return lines;
    }

    /// <summary>Who healed you, and how often. Counts and totals only — never ranked
    /// against each other and never against yours: measuring other players is the one
    /// line this project does not cross (CLAUDE.md).</summary>
    public static List<CardRow> HealerRows(StatsSnapshot s) =>
    [
        .. s.HealsByHealer.Select(h =>
            new CardRow(h.Name, $"{h.Total:N0} · {h.Hits} heal{(h.Hits == 1 ? "" : "s")}")),
    ];
}
