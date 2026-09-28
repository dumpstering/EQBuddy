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

    /// <summary>
    /// Codex QA (post-merge review of 4357760c) MAJOR finding: the replay buffer that
    /// fixes the trigger-hit bug above has no memory of what it already replayed. If
    /// Garg leaves the group (clearing TeammateRoster's auto-detected set and party-kill
    /// counters) and then earns a FRESH kill-plus-party-XP correlation from scratch while
    /// his first promotion's own trigger line is still sitting in the 32-line ring
    /// buffer, TeammateRoster.LastPartyKillPromotion fires again for "Garg" and the old
    /// replay would re-scan the WHOLE buffer — crediting the same finishing-blow line to
    /// Garg a second time.
    /// </summary>
    [Fact]
    public void ALeaveThenRePromotionWithinTheBufferWindowDoesNotDoubleCreditTheOldTriggerLine()
    {
        var duo = new OwnLogDuo();
        var t = T;
        void Feed(string msg) => duo.Feed(t = t.AddSeconds(1), msg);

        // First promotion, exactly like TheHitThatTriggersRosterRecognitionIsCreditedToThatTeammate:
        // the trigger line (177 damage) precedes the party-XP/kill lines that promote Garg.
        Feed("You gain party experience! (1.000%)");
        Feed("A rat has been slain by Garg!");
        Feed("You gain party experience! (1.000%)");
        Feed("A bat has been slain by Garg!");
        Feed("Garg punches a flouting gargoyle for 177 points of damage. (Finishing Blow)");
        Feed("You gain party experience! (1.581%)");
        Feed("A flouting gargoyle has been slain by Garg!");
        Assert.Contains("Garg", duo.Teammates.AutoDetected);
        Assert.Equal(177, duo.Teammates.Snapshots()["Garg"].MeleeDamage);

        // Garg leaves the group — TeammateRoster.Leave clears auto-detection AND the
        // party-kill correlation counters for him.
        Feed("Garg has left the group.");
        Assert.DoesNotContain("Garg", duo.Teammates.AutoDetected);

        // He earns a FRESH kill-plus-party-XP correlation from scratch (no group line
        // this time — purely the correlation, same mechanism as the first promotion).
        // The old 177-damage trigger line is still well within the 32-line ring buffer
        // (only 8 lines back at this point).
        Feed("You gain party experience! (1.000%)");
        Feed("A newt has been slain by Garg!");
        Feed("You gain party experience! (1.000%)");
        Feed("A toad has been slain by Garg!");
        Feed("You gain party experience! (1.000%)");
        Feed("A slug has been slain by Garg!");
        Assert.Contains("Garg", duo.Teammates.AutoDetected);

        // The bug: this came back 354 (177 credited twice) instead of 177.
        Assert.Equal(177, duo.Teammates.Snapshots()["Garg"].MeleeDamage);
    }

    /// <summary>
    /// Codex QA MINOR finding: the self-recoil guard for bug #2 compared the target text
    /// to the actor's name byte-exact, so a case variant of the same shape ("garg" vs
    /// "Garg") would slip through and still be double-counted as outgoing damage.
    /// </summary>
    [Fact]
    public void ASelfInflictedSpellRecoilIsRecognizedRegardlessOfCase()
    {
        var duo = new OwnLogDuo();
        var t = T;
        void Feed(string msg) => duo.Feed(t = t.AddSeconds(1), msg);

        Feed("You gain party experience! (1.000%)");
        Feed("A rat has been slain by Garg!");
        Feed("You gain party experience! (1.000%)");
        Feed("A bat has been slain by Garg!");
        Feed("You gain party experience! (1.000%)");
        Feed("A gnoll has been slain by Garg!");
        Assert.Contains("Garg", duo.Teammates.AutoDetected);

        Feed("Garg hit a will pillager for 42 points of magic damage by Lifebite.");
        // A case-variant self-recoil line — TeammatePerspective only ever rewrites lines
        // that name the exact roster entry "Garg" as the actor, so the target half alone
        // varies here to isolate the comparison this finding is about.
        Feed("Garg hit garg for 6 points of magic damage by Lifebite.");

        var garg = duo.Teammates.Snapshots()["Garg"];
        Assert.Equal(42, garg.SpellDamage);
        Assert.Equal(42, garg.DamageDealt);
        Assert.Equal(6, garg.DamageTaken);
    }

    /// <summary>
    /// Independent review REJECTED finding #3 (MINOR): the self-recoil guard DROPPED the
    /// line entirely, so a real hit that landed on the teammate went uncounted as damage
    /// taken too. It must be rewritten to land exactly like the primary's own "You hurt
    /// yourself for N points." line (a DamageTakenEvent with Self: true) — excluded from
    /// damage DEALT, but counted as damage TAKEN.
    /// </summary>
    [Fact]
    public void ASelfInflictedSpellRecoilIsCountedAsDamageTaken()
    {
        var duo = new OwnLogDuo();
        var t = T;
        void Feed(string msg) => duo.Feed(t = t.AddSeconds(1), msg);

        Feed("You gain party experience! (1.000%)");
        Feed("A rat has been slain by Garg!");
        Feed("You gain party experience! (1.000%)");
        Feed("A bat has been slain by Garg!");
        Feed("You gain party experience! (1.000%)");
        Feed("A gnoll has been slain by Garg!");
        Assert.Contains("Garg", duo.Teammates.AutoDetected);

        Feed("Garg hit a will pillager for 42 points of magic damage by Lifebite.");
        Feed("Garg hit Garg for 6 points of magic damage by Lifebite.");

        var garg = duo.Teammates.Snapshots()["Garg"];
        Assert.Equal(42, garg.SpellDamage);
        Assert.Equal(42, garg.DamageDealt);
        Assert.Equal(6, garg.DamageTaken);
    }

    /// <summary>
    /// Independent review REJECTED finding #1 (MAJOR): <c>_replayConsumedThrough</c> only
    /// ever advances on a REPLAY, never on a line a teammate was credited for LIVE while on
    /// the roster. Exactly the leave-then-re-promotion shape above, but with one extra live
    /// hit landed on Garg AFTER his first promotion and BEFORE he leaves: that hit is
    /// applied normally (live, appliedTo), then re-enters the ring buffer like every other
    /// line. When Garg is re-promoted, the marker left over from the first replay predates
    /// this line's sequence number, so the old code re-scans and re-credits it — the same
    /// 50 damage counted twice on top of the correctly-once-counted 177.
    /// </summary>
    [Fact]
    public void ALiveCreditedLineIsNeverReplayedAfterALeaveAndRePromotion()
    {
        var duo = new OwnLogDuo();
        var t = T;
        void Feed(string msg) => duo.Feed(t = t.AddSeconds(1), msg);

        // First promotion, same shape as the leave/re-promotion test above.
        Feed("You gain party experience! (1.000%)");
        Feed("A rat has been slain by Garg!");
        Feed("You gain party experience! (1.000%)");
        Feed("A bat has been slain by Garg!");
        Feed("Garg punches a flouting gargoyle for 177 points of damage. (Finishing Blow)");
        Feed("You gain party experience! (1.581%)");
        Feed("A flouting gargoyle has been slain by Garg!");
        Assert.Contains("Garg", duo.Teammates.AutoDetected);
        Assert.Equal(177, duo.Teammates.Snapshots()["Garg"].MeleeDamage);

        // A LIVE hit while Garg is still a recognized teammate — credited through the
        // normal (non-replay) path, then enters the ring buffer just like any other line.
        Feed("Garg punches a newt for 50 points of damage.");
        Assert.Equal(227, duo.Teammates.Snapshots()["Garg"].MeleeDamage);

        // Garg leaves — clears auto-detection and the party-kill correlation counters.
        Feed("Garg has left the group.");
        Assert.DoesNotContain("Garg", duo.Teammates.AutoDetected);

        // A fresh kill-plus-party-XP correlation, well within the 32-line ring buffer.
        Feed("You gain party experience! (1.000%)");
        Feed("A toad has been slain by Garg!");
        Feed("You gain party experience! (1.000%)");
        Feed("A slug has been slain by Garg!");
        Feed("You gain party experience! (1.000%)");
        Feed("A newt has been slain by Garg!");
        Assert.Contains("Garg", duo.Teammates.AutoDetected);

        // The bug: this came back 277 (177 + 50 + the already-live-credited 50 replayed
        // a second time) instead of 227.
        Assert.Equal(227, duo.Teammates.Snapshots()["Garg"].MeleeDamage);
    }

    /// <summary>
    /// Independent review REJECTED finding #1's other arm: when a name's FIRST membership
    /// came from a group-join line (not the kill-plus-party-XP correlation),
    /// <c>_replayConsumedThrough</c> is never set for them at all (stays -1). If that name
    /// later leaves and earns a FRESH kill-correlation promotion while lines from their
    /// group-join membership are still in the ring buffer, a marker of -1 makes the replay
    /// re-scan the ENTIRE buffer — re-crediting everything that was already credited live
    /// during the group-join membership.
    /// </summary>
    [Fact]
    public void AGroupJoinMembershipThenAPartyKillPromotionDoesNotReplayAlreadyLiveCreditedLines()
    {
        var duo = new OwnLogDuo();
        var t = T;
        void Feed(string msg) => duo.Feed(t = t.AddSeconds(1), msg);

        // Garg joins via an ordinary group line — never the kill-plus-party-XP
        // correlation, so LastPartyKillPromotion (and _replayConsumedThrough) never fires
        // for him during this membership.
        Feed("Garg has joined the group.");
        Assert.Contains("Garg", duo.Teammates.AutoDetected);

        // Live-credited hit while he is a normal, already-recognized member.
        Feed("Garg punches a newt for 80 points of damage.");
        Assert.Equal(80, duo.Teammates.Snapshots()["Garg"].MeleeDamage);

        Feed("Garg has left the group.");
        Assert.DoesNotContain("Garg", duo.Teammates.AutoDetected);

        // A fresh kill-plus-party-XP correlation, with the group-join membership's own
        // live-credited line still sitting in the 32-line ring buffer.
        Feed("You gain party experience! (1.000%)");
        Feed("A toad has been slain by Garg!");
        Feed("You gain party experience! (1.000%)");
        Feed("A slug has been slain by Garg!");
        Feed("You gain party experience! (1.000%)");
        Feed("A rat has been slain by Garg!");
        Assert.Contains("Garg", duo.Teammates.AutoDetected);

        // The bug: a -1 marker rescans the whole buffer and re-credits the 80-damage line
        // a second time (160) instead of leaving it at 80.
        Assert.Equal(80, duo.Teammates.Snapshots()["Garg"].MeleeDamage);
    }

    /// <summary>
    /// Regression guard for combat/session bookkeeping across a leave-then-re-promotion
    /// cycle, covering the shape independent review's finding #2 (MINOR, REJECTED)
    /// described: the "keep every known teammate's session clock moving" tick in
    /// <c>ObserveCore</c> fires for every KNOWN teammate on every primary line, whether or
    /// not they are on the current roster — so a teammate who has left keeps having
    /// <c>_lastApplied</c> pushed forward while absent — and when they are re-promoted, the
    /// ring buffer replay applies a hit stamped EARLIER than that already-advanced clock.
    ///
    /// <b>This is NOT a discriminating proof of the <c>_lastApplied</c> max() guard</b>
    /// (see <see cref="DerivedTeammates.ReplayBufferedLinesFor"/>'s own comment on why): it
    /// passes unchanged even on 1158d20d, before that guard existed, because the promoting
    /// kill line itself is applied immediately afterward through the NORMAL live path
    /// (<see cref="DerivedTeammates.ApplyRewrittenLines"/>), which unconditionally sets
    /// <c>_lastApplied[name]</c> forward to "now" regardless of what the replay left behind.
    /// So the final, observable bookkeeping below is dominated by that live-path write
    /// either way, and this test cannot tell a guarded replay from an unguarded one. What it
    /// DOES pin is that the whole leave/re-promotion round-trip — the buffered, backdated
    /// trigger hit plus the immediately-following live promotion — leaves the teammate's
    /// damage total, last-applied timestamp and combat-second accounting exactly right.
    ///
    /// Values below are hand-verified against the fixed algorithm's own (documented) rules
    /// rather than a live "always on the roster" oracle: an always-on-roster oracle would
    /// also credit Garg with a full KillEvent (and its own combat-window extension) for
    /// EVERY correlation-building kill line ("A toad/slug has been slain by Garg!") before
    /// he is actually recognized — something the real, deferred-promotion path correctly
    /// never does (a buffered KillEvent is refused rather than replayed — see
    /// <see cref="DerivedTeammates.ReplayBufferedLinesFor"/>'s own comment on that), so
    /// comparing against it would fail on a difference that is correct, not a regression.
    /// </summary>
    [Fact]
    public void ALeaveThenRePromotionReplayLeavesCombatBookkeepingConsistent()
    {
        var duo = new OwnLogDuo();
        var t = T;
        DateTime Next() => t = t.AddSeconds(1);
        void Feed(string msg) => duo.Feed(Next(), msg);

        // First promotion: the classic trigger-hit shape (177 damage), replayed once Garg
        // is recognized.
        Feed("You gain party experience! (1.000%)");
        Feed("A rat has been slain by Garg!");
        Feed("You gain party experience! (1.000%)");
        Feed("A bat has been slain by Garg!");
        Feed("Garg punches a flouting gargoyle for 177 points of damage. (Finishing Blow)");
        Feed("You gain party experience! (1.581%)");
        Feed("A flouting gargoyle has been slain by Garg!");
        Assert.Contains("Garg", duo.Teammates.AutoDetected);
        Assert.Equal(177, duo.Teammates.Snapshots()["Garg"].MeleeDamage);

        Feed("Garg has left the group.");
        Assert.DoesNotContain("Garg", duo.Teammates.AutoDetected);

        // Twenty quiet seconds for Garg while the primary keeps playing alone — comfortably
        // past SessionStats' own 10-second combat gap, so his first combat span (above)
        // closes, AND comfortably advances the keep-alive clock for him (still known, just
        // off-roster) to "now" before the second promotion below ever happens.
        for (var i = 0; i < 20; i++) Feed($"You have taken {i + 1} damage from a rat by Claw.");

        // A fresh kill-plus-party-XP correlation. Its own trigger hit (90 damage) is
        // buffered and stamped several seconds BEHIND the keep-alive-advanced clock — the
        // exact out-of-order shape the finding describes. The two correlation-building kill
        // lines ("toad", "slug") happen while Garg is still unrecognized, so — correctly —
        // neither is ever credited to him (a buffered KillEvent is refused, and neither line
        // is re-visited once he IS recognized): only the trigger hit and the final,
        // promoting kill line ("newt") ever reach his stats.
        Feed("You gain party experience! (1.000%)");
        Feed("A toad has been slain by Garg!");
        Feed("You gain party experience! (1.000%)");
        Feed("A slug has been slain by Garg!");
        var trigger = Next(); duo.Feed(trigger, "Garg punches a newt for 90 points of damage. (Finishing Blow)");
        Feed("You gain party experience! (1.000%)");
        var lastLine = Next(); duo.Feed(lastLine, "A newt has been slain by Garg!");
        Assert.Contains("Garg", duo.Teammates.AutoDetected);

        var garg = duo.Teammates.Snapshots()["Garg"];

        // 177 + 90: the trigger hit is credited exactly once on each promotion.
        Assert.Equal(267, garg.MeleeDamage);

        // The bookkeeping must not be left stale by the older, backdated replay: the most
        // recent thing that happened to Garg is the promoting kill line itself, applied
        // forward (normally) immediately after the replay in the very same call.
        Assert.Equal(lastLine, garg.LastEventTime);

        // Combat spans: the first span (rat/bat/gargoyle-kill/177-hit) runs from the 177
        // hit to the promoting kill line 2 seconds later (2s); it closes on the 20-second
        // quiet gap that follows. The second span opens fresh at the (backdated) 90-hit and
        // runs to the promoting "newt" kill line 2 seconds later (2s) — it is still open at
        // snapshot time. A corrupted (regressed) clock would either shrink one of these
        // spans to nothing, extend the closed span across the quiet gap, or otherwise throw
        // this off; the fixed algorithm gives exactly 2 + 2 = 4 seconds.
        Assert.Equal(4.0, garg.CombatSeconds, 3);
        Assert.Equal(267.0 / 4.0, garg.SessionDps, 3);
    }

    /// <summary>
    /// Independent review REJECTED finding #4 (MINOR): <c>CommitReplay</c> swapped in the
    /// staging instance's <c>_stats</c>/<c>_roster</c> (and the corrections dictionaries)
    /// but left this instance's OWN <c>_recentLines</c> ring buffer, <c>_nextLineSeq</c> and
    /// <c>_replayConsumedThrough</c> markers untouched — bookkeeping that describes staging's
    /// replay left behind, next to state describing a session that no longer exists.
    ///
    /// Reproduced here at its simplest: a re-derivation (<see cref="DerivedTeammates.BeginReplay"/>
    /// / <see cref="DerivedTeammates.CommitReplay"/>, the same pair
    /// <see cref="LogWatcher.RederiveTeammates"/> uses) is committed WHILE a party-kill
    /// correlation's own trigger hit is still sitting unconsumed in the STAGING instance's
    /// buffer — the promoting kill line itself has not been fed yet. Without carrying that
    /// buffer over, the live instance has no record of the trigger hit at all once the
    /// promoting line finally arrives (fed to the now-committed live instance): the
    /// teammate's damage is silently short by exactly the buffered hit. And the buffer/marker
    /// pair must stay CONSISTENT going forward too — a second, later leave-and-re-promotion
    /// on the live instance must credit its own (separate) trigger hit exactly once, proving
    /// the carried-over bookkeeping does not also open the door to a double credit.
    /// </summary>
    [Fact]
    public void ARederiveCommittedWithAPendingTriggerHitStillCreditsItExactlyOnce()
    {
        var duo = new OwnLogDuo();
        var (staging, generation) = duo.Teammates.BeginReplay();

        var st = T;
        DateTime SNext() => st = st.AddSeconds(1);
        void SFeed(string msg)
        {
            var ts = SNext();
            staging.ObservePrimaryLine(ts, msg, LogParser.Parse(ts, msg));
        }

        // Two correlation-building kills plus the trigger hit, fed to STAGING — Garg is not
        // yet promoted there (only 2 of the 3 required kills have landed), so the trigger
        // hit sits in staging's own ring buffer, unconsumed, at the moment of commit.
        SFeed("You gain party experience! (1.000%)");
        SFeed("A rat has been slain by Garg!");
        SFeed("You gain party experience! (1.000%)");
        SFeed("A bat has been slain by Garg!");
        SFeed("Garg punches a flouting gargoyle for 177 points of damage. (Finishing Blow)");

        Assert.True(duo.Teammates.CommitReplay(staging, generation));
        Assert.DoesNotContain("Garg", duo.Teammates.AutoDetected);

        var t = st;
        DateTime Next() => t = t.AddSeconds(1);
        void Feed(string msg) => duo.Feed(Next(), msg);

        // The promoting kill line lands on the now-committed LIVE instance. The bug: with
        // no carried-over buffer, this credits nothing for the 177 hit — it simply isn't
        // there to find.
        Feed("You gain party experience! (1.581%)");
        Feed("A flouting gargoyle has been slain by Garg!");
        Assert.Contains("Garg", duo.Teammates.AutoDetected);
        Assert.Equal(177, duo.Teammates.Snapshots()["Garg"].MeleeDamage);

        // A second, independent leave-and-re-promotion on the live instance, entirely after
        // the commit — its own trigger hit must land exactly once on top of the first.
        Feed("Garg has left the group.");
        Feed("You gain party experience! (1.000%)");
        Feed("A toad has been slain by Garg!");
        Feed("You gain party experience! (1.000%)");
        Feed("A slug has been slain by Garg!");
        Feed("Garg punches a newt for 90 points of damage. (Finishing Blow)");
        Feed("You gain party experience! (1.000%)");
        Feed("A newt has been slain by Garg!");

        Assert.Equal(267, duo.Teammates.Snapshots()["Garg"].MeleeDamage);
    }
}
