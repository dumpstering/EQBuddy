using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>
/// One test per behavioural finding fixed on own-log-teammates after the wiring/audit
/// pass — each reproduces the exact bug shape the finding described and fails without
/// its fix.
/// </summary>
public class TeammateFixesTests
{
    private const string Primary = "Smargush";
    private static readonly DateTime T = new(2026, 1, 1, 12, 0, 0);

    // ---------------------------------------------------------------------
    // TeammateRoster: an invite alone is not membership.
    // ---------------------------------------------------------------------
    [Fact]
    public void InviteAloneNeverPromotesTheInviter()
    {
        var derived = new DerivedTeammates();
        derived.Observe(T, "Sangsong invites you to join a group.", Primary, null, null);
        derived.Observe(T.AddSeconds(1),
            "Sangsong pierces a necro acolyte for 59 points of damage.", Primary, null, null);
        derived.Observe(T.AddSeconds(2),
            "A necro acolyte has been slain by Sangsong!", Primary, null, null);

        var roster = derived.Roster(Primary, null, null);
        Assert.DoesNotContain("Sangsong", roster);

        var combined = TeammateCombine.Combine(NewPrimarySnapshot(), derived.Snapshots(Primary, null, null));
        Assert.Equal(0, combined.YourKillCount);   // the bystander's kill never became "yours"
    }

    // ---------------------------------------------------------------------
    // TeammateRoster: agreeing to an invite DOES promote it.
    // ---------------------------------------------------------------------
    [Fact]
    public void AgreeingToAnInviteJoinsTheRoster()
    {
        var derived = new DerivedTeammates();
        derived.Observe(T, "Sangsong invites you to join a group.", Primary, null, null);
        derived.Observe(T.AddSeconds(1),
            "You notify Sangsong that you agree to join the group.", Primary, null, null);

        Assert.Contains("Sangsong", derived.Roster(Primary, null, null));
    }

    // ---------------------------------------------------------------------
    // TeammateRoster: leaving the group removes the name from the ACTIVE roster.
    // ---------------------------------------------------------------------
    [Fact]
    public void LeavingTheGroupStopsFutureLinesCountingAsTeammateActivity()
    {
        var derived = new DerivedTeammates();
        derived.Observe(T, "Ripto has joined the group.", Primary, null, null);
        derived.Observe(T.AddSeconds(1), "Ripto has left the group.", Primary, null, null);
        derived.Observe(T.AddSeconds(2),
            "Ripto slashes a gnoll for 100 points of damage.", Primary, null, null);
        derived.Observe(T.AddSeconds(3), "A gnoll has been slain by Ripto!", Primary, null, null);

        Assert.DoesNotContain("Ripto", derived.Roster(Primary, null, null));
        var combined = TeammateCombine.Combine(NewPrimarySnapshot(), derived.Snapshots(Primary, null, null));
        Assert.Equal(0, combined.YourKillCount);
    }

    // ---------------------------------------------------------------------
    // DerivedTeammates (audit finding, major): leaving the group must not erase
    // stats already accrued while the teammate WAS a member — only stop FUTURE
    // lines from being attributed to them.
    // ---------------------------------------------------------------------
    [Fact]
    public void LeavingTheGroupDoesNotLoseStatsAlreadyAccrued()
    {
        var derived = new DerivedTeammates();
        derived.Observe(T, "Ripto has joined the group.", Primary, null, null);
        derived.Observe(T.AddSeconds(1),
            "Ripto slashes a gnoll for 250 points of damage.", Primary, null, null);
        derived.Observe(T.AddSeconds(2), "Ripto has left the group.", Primary, null, null);

        Assert.DoesNotContain("Ripto", derived.Roster(Primary, null, null));

        var mates = derived.Snapshots(Primary, null, null);
        Assert.True(mates.ContainsKey("Ripto"));
        Assert.Equal(250, mates["Ripto"].DamageDealt);

        var combined = TeammateCombine.Combine(NewPrimarySnapshot(), mates);
        Assert.Equal(250, combined.DamageDealt);   // not lost just because Ripto left
    }

    // ---------------------------------------------------------------------
    // TeammateRoster (audit finding, major): a real, observed log line —
    // "You remove <YourName> from the party." — is how the client logs the
    // PRIMARY's own /disband, grammatically identical to removing a groupmate but
    // with the PRIMARY's own name in the slot. It must clear the whole roster, not
    // be read as "remove a member named <YourName>" (a no-op, since the primary is
    // never in their own roster) that leaves every real groupmate still counting.
    // ---------------------------------------------------------------------
    [Fact]
    public void SelfDisbandLineClearsTheRosterRatherThanBeingANoOp()
    {
        var derived = new DerivedTeammates();
        derived.Observe(T, "Kellisanth has joined the group.", Primary, null, null);
        derived.Observe(T.AddSeconds(1), $"You remove {Primary} from the party.", Primary, null, null);
        derived.Observe(T.AddSeconds(2),
            "Kellisanth slashes a rat for 500 points of damage.", Primary, null, null);

        Assert.DoesNotContain("Kellisanth", derived.Roster(Primary, null, null));
        var combined = TeammateCombine.Combine(NewPrimarySnapshot(), derived.Snapshots(Primary, null, null));
        Assert.Equal(0, combined.DamageDealt);   // the post-disband hit is a bystander's, not "yours"
    }

    // ---------------------------------------------------------------------
    // TeammateRoster: "You have been removed from the group." clears everyone.
    // ---------------------------------------------------------------------
    [Fact]
    public void BeingRemovedFromTheGroupClearsTheWholeRoster()
    {
        var derived = new DerivedTeammates();
        derived.Observe(T, "Garg has joined the group.", Primary, null, null);
        derived.Observe(T.AddSeconds(1), "Kellisanth has joined the group.", Primary, null, null);
        derived.Observe(T.AddSeconds(2), "You have been removed from the group.", Primary, null, null);

        var roster = derived.Roster(Primary, null, null);
        Assert.DoesNotContain("Garg", roster);
        Assert.DoesNotContain("Kellisanth", roster);
    }

    // ---------------------------------------------------------------------
    // TeammatePerspective: manual roster names match the log regardless of case.
    // ---------------------------------------------------------------------
    [Fact]
    public void ManuallyTypedLowercaseNameStillMatchesTheLog()
    {
        var derived = new DerivedTeammates();
        derived.Observe(T, "Garg slashes a froglok for 91 points of damage.",
            Primary, null, ["garg"]);

        var mates = derived.Snapshots(Primary, null, ["garg"]);
        Assert.True(mates.ContainsKey("Garg"));
        Assert.Equal(91, mates["Garg"].DamageDealt);
    }

    // ---------------------------------------------------------------------
    // TeammatePerspective / DerivedTeammates: a teammate's warder taking damage does
    // NOT inflate the owner's own DamageTaken.
    // ---------------------------------------------------------------------
    [Fact]
    public void WarderDamageTakenIsNotFoldedIntoTheOwner()
    {
        var derived = new DerivedTeammates();
        var roster = new[] { "Sinista" };
        derived.Observe(T, "A gnoll cleaves Sinista`s warder for 13 points of damage.",
            Primary, null, roster);

        var mates = derived.Snapshots(Primary, null, roster);
        // Either nothing was created for Sinista, or if it was (via the session tick),
        // the object-form pet hit must not have raised DamageTaken.
        if (mates.TryGetValue("Sinista", out var snap))
            Assert.Equal(0, snap.DamageTaken);
    }

    // ---------------------------------------------------------------------
    // TeammatePerspective / DerivedTeammates: a teammate's warder casting is not
    // folded into the owner's own cast count.
    // ---------------------------------------------------------------------
    [Fact]
    public void WarderCastIsNotFoldedIntoTheOwnersCasts()
    {
        var derived = new DerivedTeammates();
        var roster = new[] { "Kellisanth" };
        derived.Observe(T, "Kellisanth`s warder begins casting Minor Healing.", Primary, null, roster);
        derived.Observe(T.AddSeconds(1), "Kellisanth slashes a gnoll for 10 points of damage.",
            Primary, null, roster);

        var mates = derived.Snapshots(Primary, null, roster);
        Assert.Equal(0, mates["Kellisanth"].CastsStarted);
    }

    // ---------------------------------------------------------------------
    // TeammatePerspective: a warder healing its OWNER is not double-counted as
    // HealingReceived (the "+147" bug).
    // ---------------------------------------------------------------------
    [Fact]
    public void WarderHealingItsOwnerCountsOnce()
    {
        var derived = new DerivedTeammates();
        var roster = new[] { "Kellisanth" };
        const string line = "Kellisanth`s warder healed Kellisanth for 147 hit points by Healing.";

        derived.Observe(T, line, Primary, null, roster);

        var snap = derived.Snapshots(Primary, null, roster)["Kellisanth"];
        Assert.Equal(147, snap.HealingDone);
        Assert.Equal(147, snap.HealingReceived);   // not 294
    }

    // ---------------------------------------------------------------------
    // TeammatePerspective: a warder healing ITSELF does not read as the owner
    // receiving healing.
    // ---------------------------------------------------------------------
    [Fact]
    public void WarderHealingItselfIsNotCreditedToTheOwnerAsReceived()
    {
        var derived = new DerivedTeammates();
        var roster = new[] { "Kanaddar" };
        const string line = "Kanaddar`s warder healed itself for 20 hit points by Inner Fire.";

        derived.Observe(T, line, Primary, null, roster);

        var snap = derived.Snapshots(Primary, null, roster)["Kanaddar"];
        Assert.Equal(0, snap.HealingReceived);
    }

    // ---------------------------------------------------------------------
    // DerivedTeammates: a teammate quiet for over an hour, while the primary keeps
    // playing REAL EVENTS (no primary-side gap), does not lose their earlier
    // contribution — "You look around." (no event) would not do it: only a line the
    // primary itself applies an event for may keep a teammate's clock alive on the
    // primary's behalf (see Observe's own doc).
    // ---------------------------------------------------------------------
    [Fact]
    public void AQuietTeammateDoesNotLoseEarlierStatsMidPrimarySession()
    {
        var derived = new DerivedTeammates();
        var roster = new[] { "Garg" };

        derived.Observe(T, "Garg slashes a gnoll for 500 points of damage.", Primary, null, roster);
        derived.Observe(T.AddMinutes(1), "A gnoll has been slain by Garg!", Primary, null, roster);

        // The primary keeps ticking every 10 minutes so ITS OWN session never gaps —
        // a real parsed event each time — while Garg says nothing the whole time.
        for (var i = 1; i <= 9; i++)
            derived.Observe(T.AddMinutes(10 * i), "You slash a rat for 1 points of damage.", Primary, null, roster);

        // Garg speaks again at +91 minutes — over an hour of Garg-silence, but the
        // PRIMARY never had a 60-minute gap.
        derived.Observe(T.AddMinutes(91), "Garg slashes a gnoll for 5 points of damage.",
            Primary, null, roster);

        var snap = derived.Snapshots(Primary, null, roster)["Garg"];
        Assert.Equal(505, snap.DamageDealt);   // 500 + 5, not just 5
        Assert.Equal(1, snap.YourKillCount);
    }

    // ---------------------------------------------------------------------
    // DerivedTeammates (audit finding, major): a genuine multi-hour gap in the
    // PRIMARY's own real activity — ordinary chat flowing the whole time, nothing
    // that parses into an event or matches a Text rule — must roll a teammate over
    // exactly as it would roll the primary over. Before the fix, DerivedTeammates
    // ticked every known teammate's clock on EVERY raw line regardless of whether it
    // parsed, so the chat itself (arriving every few minutes) kept Garg artificially
    // "fresh" through a gap that is, from the primary's own perspective, event-silent
    // and therefore session-rolling.
    // ---------------------------------------------------------------------
    [Fact]
    public void AnEventSilentChattyGapRollsTheTeammateOverLikeThePrimaryWould()
    {
        var derived = new DerivedTeammates();
        var roster = new[] { "Garg" };

        derived.Observe(T, "Garg slashes a gnoll for 500 points of damage.", Primary, null, roster);
        derived.Observe(T.AddMinutes(1), "A gnoll has been slain by Garg!", Primary, null, roster);

        // 70 minutes of ordinary chat, none of it parseable and none of it naming
        // Garg — every 5 minutes, so under the OLD (unconditional) tick this never
        // let 60 minutes pass between two ticks, and the bug never rolled Garg over.
        for (var i = 1; i <= 14; i++)
            derived.Observe(T.AddMinutes(5 * i), $"Someone tells the guild, 'chatter {i}'", Primary, null, roster);

        derived.Observe(T.AddMinutes(71), "Garg slashes a gnoll for 5 points of damage.", Primary, null, roster);

        var snap = derived.Snapshots(Primary, null, roster)["Garg"];
        // A genuine 70-minute gap in real activity must roll Garg's session exactly
        // like the primary's own would — 5, not 505.
        Assert.Equal(5, snap.DamageDealt);
        Assert.Equal(0, snap.YourKillCount);
    }

    // ---------------------------------------------------------------------
    // DerivedTeammates: a teammate silent across a genuine primary-session gap does
    // not carry stale data into the fresh session.
    // ---------------------------------------------------------------------
    [Fact]
    public void APrimarySessionGapRollsTheTeammateOverToo()
    {
        var derived = new DerivedTeammates();
        var roster = new[] { "Garg" };

        derived.Observe(T, "Garg slashes a gnoll for 500 points of damage.", Primary, null, roster);
        derived.Observe(T.AddMinutes(1), "A gnoll has been slain by Garg!", Primary, null, roster);

        // A real multi-hour gap, primary side included.
        derived.Observe(T.AddHours(3), "You slash a rat for 10 points of damage.", Primary, null, roster);

        var mates = derived.Snapshots(Primary, null, roster);
        // Garg's own instance never received a fresh line naming him, so it holds no
        // snapshot for the new session (the old one has rolled off by the tick, and
        // nothing since has re-created it) — the stale 500/1 must not still be there.
        if (mates.TryGetValue("Garg", out var snap))
        {
            Assert.Equal(0, snap.DamageDealt);
            Assert.Equal(0, snap.YourKillCount);
        }
    }

    // ---------------------------------------------------------------------
    // TeammateCombine: PartyKillsByKiller sums to PartyKillCount after a warder kill
    // is promoted into the teammate's own kills.
    // ---------------------------------------------------------------------
    [Fact]
    public void WarderKillerRowIsRemovedAfterItsKillIsPromoted()
    {
        var primary = new SessionStats { CharacterName = Primary };
        var derived = new DerivedTeammates();
        var roster = new[] { "Kellisanth" };
        const string line = "A gnoll has been slain by Kellisanth`s warder!";

        primary.Apply(LogParser.Parse(T, line)!);
        derived.Observe(T, line, Primary, null, roster);

        var combined = TeammateCombine.Combine(primary.Snapshot(), derived.Snapshots(Primary, null, roster));

        Assert.Equal(1, combined.YourKillCount);
        Assert.Equal(0, combined.PartyKillCount);
        Assert.DoesNotContain(combined.PartyKillsByKiller,
            nc => nc.Name.Equals("Kellisanth`s warder", StringComparison.OrdinalIgnoreCase));
        // Every killer row must now sum to PartyKillCount.
        Assert.Equal(combined.PartyKillCount, combined.PartyKillsByKiller.Sum(nc => nc.Count));
    }

    // ---------------------------------------------------------------------
    // TeammateCombine: the live-instance overload computes an exact combat-seconds
    // UNION instead of Math.Max, even for a single derived teammate.
    // ---------------------------------------------------------------------
    [Fact]
    public void CombatSecondsIsTheUnionNotTheMaxEvenForOneTeammate()
    {
        var primary = new SessionStats { CharacterName = Primary };
        var derived = new DerivedTeammates();
        var roster = new[] { "Garg" };

        // Primary fights for a 10-second span...
        primary.Apply(LogParser.Parse(T, "You slash a froglok for 10 points of damage.")!);
        primary.Apply(LogParser.Parse(T.AddSeconds(10), "You slash a froglok for 10 points of damage.")!);

        // ...and Garg fights a DISJOINT 10-second span two minutes later, while the
        // primary is idle (a real gap between the two fights, not a single overlapping
        // one) — Math.Max would read this as 10s total; the true union is ~20s.
        var laterStart = T.AddMinutes(2);
        derived.Observe(laterStart, "Garg slashes a gnoll for 20 points of damage.", Primary, null, roster);
        derived.Observe(laterStart.AddSeconds(10), "Garg slashes a gnoll for 20 points of damage.",
            Primary, null, roster);

        var liveTeammates = derived.LiveStats(Primary, null, roster);
        var combined = TeammateCombine.Combine(primary, liveTeammates);

        Assert.True(combined.CombatSeconds >= 19,
            $"expected the union of two disjoint ~10s spans (~20s), got {combined.CombatSeconds}");
    }

    private static StatsSnapshot NewPrimarySnapshot() => new SessionStats { CharacterName = Primary }.Snapshot();
}
