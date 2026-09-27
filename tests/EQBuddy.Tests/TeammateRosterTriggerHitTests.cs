using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>
/// The canary (an independent regex ground truth run over real log windows,
/// EQBuddy-sync/TEST-REPORT.md 2026-09-27) found Garg's melee damage short by exactly
/// 177 in two separate windows, isolated to one line: a "Finishing Blow" kill line is the
/// SAME kill whose "X has been slain by Garg!" line gives the roster's kill-plus-party-XP
/// correlation (<see cref="TeammateRoster.PartyKillsToJoin"/>) its third and deciding
/// piece of evidence for Garg. The melee hit that actually finished the mob is a separate,
/// EARLIER log line than the kill line, so at the moment it was read Garg was not yet on
/// the roster and the line's damage was silently dropped — never rewritten for anybody,
/// never re-visited once the roster caught up two lines later.
///
/// This synthetic excerpt reproduces the shape (not the content) of the real fixture
/// lines from canary/fixtures/window-a-early-raid.txt around 01:22:32: two prior
/// party-XP-correlated kills to bring Garg to the correlation's threshold, then a third
/// whose OWN finishing blow precedes the party-XP and kill lines that promote him.
/// </summary>
public sealed class TeammateRosterTriggerHitTests
{
    private static readonly DateTime T = new(2026, 9, 27, 1, 22, 0);

    [Fact]
    public void TheHitThatTriggersRosterRecognitionIsCreditedToThatTeammate()
    {
        var duo = new OwnLogDuo();
        var t = T;
        void Feed(string msg) => duo.Feed(t = t.AddSeconds(1), msg);

        // Kill #1 and #2: ordinary party-XP-correlated kills, building Garg's count
        // toward TeammateRoster.PartyKillsToJoin (3). Garg is not yet a recognized
        // teammate for either of these — nothing here should ever be credited to him.
        Feed("You gain party experience! (1.000%)");
        Feed("A rat has been slain by Garg!");
        Feed("You gain party experience! (1.000%)");
        Feed("A bat has been slain by Garg!");

        // Kill #3: the finishing blow itself deals the damage, on a line BEFORE the
        // party-XP and kill lines that actually promote Garg onto the roster — exactly
        // the real log's shape.
        Feed("Garg punches a flouting gargoyle for 177 points of damage. (Finishing Blow)");
        Feed("You gain party experience! (1.581%)");
        Feed("A flouting gargoyle has been slain by Garg!");

        Assert.Contains("Garg", duo.Teammates.AutoDetected);

        var snapshots = duo.Teammates.Snapshots();
        Assert.True(snapshots.ContainsKey("Garg"), "Garg should have an isolated SessionStats once recognized.");
        var garg = snapshots["Garg"];

        // The bug: this came back 0 (the "Finishing Blow" line was read and discarded
        // before Garg existed in the roster, and nothing ever went back for it).
        Assert.Equal(177, garg.MeleeDamage);
        Assert.Equal(177, garg.DamageDealt);
    }

    /// <summary>
    /// A second canary residual (TEST-REPORT.md, window-b-full-session only): Garg's
    /// spell damage read +6 over the independent ground truth. The disputed line, found
    /// by diffing every "Garg hit ... by ..." line against gt.py's own regex: "Garg hit
    /// Garg for 6 points of magic damage by Lifebite." — a life-tap that recoiled onto
    /// its own caster (EQ's third-person rendering has no reflexive pronoun for this
    /// shape, unlike the heal/miss shapes, which do say "himself"/"herself"; a real,
    /// verbatim line, not a hypothetical). gt.py deliberately excludes any hit whose
    /// target resolves to the ACTOR who dealt it (its SELF_TARGETS set) from that actor's
    /// own "dealt" total — the same way EQBuddy already excludes the primary's own
    /// "You hurt yourself for N points." from DamageDealt. TeammatePerspective's
    /// third-person-to-first-person rewrite had no such check: it rewrote the line
    /// literally into "You hit Garg for 6 points of magic damage by Lifebite.", which
    /// LogParser (correctly, from its own perspective — it has no way to know "Garg" is
    /// the caster's own name) parsed as 6 more points of ordinary outgoing spell damage
    /// against an opponent that does not exist.
    /// </summary>
    [Fact]
    public void ASelfInflictedSpellRecoilIsNotCountedAsDamageDealt()
    {
        var duo = new OwnLogDuo();
        var t = T;
        void Feed(string msg) => duo.Feed(t = t.AddSeconds(1), msg);

        // Bring Garg onto the roster the ordinary way (three party-XP-correlated kills)
        // before the disputed line, so it lands on an already-recognized teammate.
        Feed("You gain party experience! (1.000%)");
        Feed("A rat has been slain by Garg!");
        Feed("You gain party experience! (1.000%)");
        Feed("A bat has been slain by Garg!");
        Feed("You gain party experience! (1.000%)");
        Feed("A gnoll has been slain by Garg!");
        Assert.Contains("Garg", duo.Teammates.AutoDetected);

        // An ordinary outgoing hit, so the test can tell "excluded" from "never counted
        // at all" — this one MUST still be credited.
        Feed("Garg hit a will pillager for 42 points of magic damage by Lifebite.");

        // The disputed self-recoil line.
        Feed("Garg hit Garg for 6 points of magic damage by Lifebite.");

        var garg = duo.Teammates.Snapshots()["Garg"];
        Assert.Equal(42, garg.SpellDamage);
        Assert.Equal(42, garg.DamageDealt);
    }
}
