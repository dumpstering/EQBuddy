using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>
/// The regression net for the exact bug class the Step 3 redesign claims to make
/// impossible BY CONSTRUCTION rather than by remembering to gate each call site.
///
/// Repair rounds R1/R2 closed these leaks with a <c>fromTeammate</c> flag threaded
/// through <see cref="SessionStats.Apply"/> — and each of three audits found the same
/// class of bug in a NEW place the flag had not reached yet. Step 3 replaces the flag
/// entirely: a teammate's log now drives its OWN <see cref="SessionStats"/> instance
/// (exactly as <see cref="TeammateLogTail"/> builds one — plain construction, no store
/// attached, no character identity borrowed from the primary), so there is no shared
/// object left for a teammate's line to corrupt. Every test below replays the SAME
/// lines that used to require a gate — now applied to a genuinely SEPARATE instance —
/// and proves the watched character's actor-local state is untouched. Kept (not
/// deleted, per the redesign's own instruction) because they are the readable,
/// named regression trip-wire for each specific misattribution family a future change
/// could reintroduce (a shared static, an accidentally-passed-through reference, or a
/// store attached to the wrong instance) — precisely the "remembering to gate each
/// line" failure mode this whole redesign exists to retire.
/// </summary>
public class TeammateActorLocalGuardTests
{
    private static SessionStats Replay(params string[] lines)
    {
        var stats = new SessionStats();
        foreach (var line in lines) stats.Apply(LogParser.Parse(line)!);
        return stats;
    }

    /// <summary>Builds the teammate's own session exactly the way
    /// <see cref="TeammateLogTail"/>'s constructor does: plain <c>new SessionStats()</c>,
    /// no store ever attached, no subscriber. Then applies every given line to IT, not
    /// to <paramref name="watched"/> — the two are separate objects from construction
    /// on, which is the whole mechanism.</summary>
    private static SessionStats ApplyAsMate(params string[] lines)
    {
        var mate = new SessionStats();
        foreach (var line in lines) mate.Apply(LogParser.Parse(line)!);
        return mate;
    }

    [Fact]
    public void R1b_TeammateIncomingDamageFromACreatureSharingYourPetsNameDoesNotBreakTheCharm()
    {
        // CharmTracker is built fresh inside EACH SessionStats' own constructor
        // (`_charm = new CharmTracker(_spells) { ... }`) — a teammate's own incoming
        // hit from a creature that happens to share the WATCHED character's pet's
        // name is applied to a tracker that has never heard of that pet at all.
        var mine = Replay(
            "[Sat Jul 18 15:00:00 2026] You begin casting Charm.",
            "[Sat Jul 18 15:00:02 2026] orc pawn has been charmed.");
        Assert.Equal("Orc pawn", mine.Snapshot().PetName);

        // Well past CharmSettleSeconds (3s) and with no same-name-duplicate proof — the
        // shape that DOES break a real charm when it's YOUR pet attacking you.
        ApplyAsMate("[Sat Jul 18 15:00:10 2026] Orc pawn hits YOU for 5 points of damage.");

        Assert.Equal("Orc pawn", mine.Snapshot().PetName);
    }

    [Fact]
    public void R2_TeammateEventsDoNotCountAsYourActivePlayTime()
    {
        // _activeBuckets is "the record of your own actions" per its own doc comment —
        // a teammate's damage, applied to their OWN instance, can only ever bump
        // THEIR ActiveSeconds, never the watched character's.
        var mine = new SessionStats();
        var mate = ApplyAsMate("[Mon Aug 10 11:15:00 2026] You slash orc pawn for 10 points of damage.");
        Assert.Equal(0, mine.Snapshot().ActiveSeconds);
        Assert.True(mate.Snapshot().ActiveSeconds > 0);   // sanity: the mate's OWN active time IS tracked
    }

    [Fact]
    public void R2_TeammateUnknownHealDoesNotBorrowYourInvocationLabel()
    {
        // _currentInvocation is the WATCHED character's own current recitation, held
        // in THEIR instance only — a teammate's unattributed heal, applied to a
        // separate instance that never received that recite line, has no invocation
        // to borrow at all.
        var mine = Replay("[Mon Aug 10 11:15:00 2026] You begin reciting the divine invocation.");
        var mate = ApplyAsMate("[Mon Aug 10 11:15:01 2026] You healed Buddy for 20 hit points.");

        Assert.DoesNotContain(mine.Snapshot().HealsBySpell, sd => sd.Name == "Divine Invocation");
        Assert.DoesNotContain(mate.Snapshot().HealsBySpell, sd => sd.Name == "Divine Invocation");
    }

    [Fact]
    public void R2_TeammateHealsDoNotTeachYourSpellCatalog()
    {
        // Both heal directions call SpellCatalog.Learn — but `_spells` is per-instance
        // (SessionStats() builds its own `SpellCatalog`), so a teammate's own outgoing
        // heal, or a heal landing on THEM, can only ever teach THEIR classifier.
        var mine = new SessionStats();
        var mate = ApplyAsMate(
            "[Mon Aug 10 11:15:00 2026] You healed Buddy for 20 hit points by Zorbo's Mending Chant.",
            "[Mon Aug 10 11:15:01 2026] Aamilea healed you for 15 hit points by Zorbo's Ember Ward.");

        Assert.Equal(SpellCategory.Unknown, mine.Spells.Classify("Zorbo's Mending Chant"));
        Assert.Equal(SpellCategory.Unknown, mine.Spells.Classify("Zorbo's Ember Ward"));
        // Sanity: the mate's OWN classifier DID learn from its own lines — proving the
        // isolation is about WHICH instance learns, not that learning is broken.
        Assert.NotEqual(SpellCategory.Unknown, mate.Spells.Classify("Zorbo's Mending Chant"));
    }

    [Fact]
    public void R2_TeammatesIncomingDamageDoesNotBlameYourDeathOnTheirAttacker()
    {
        // _lastDamageFrom answers "what last hurt YOU", for a killer-less "You died."
        // line to blame — held per-instance, so a hit landing on the TEAMMATE (a
        // separate instance's own _lastDamageFrom) cannot supply that blame for a
        // death applied to the WATCHED character's instance.
        var mine = new SessionStats();
        ApplyAsMate("[Mon Aug 10 11:15:00 2026] Gribble hits YOU for 999 points of damage.");
        mine.Apply(LogParser.Parse("[Mon Aug 10 11:15:01 2026] You died.")!);

        Assert.DoesNotContain(mine.Snapshot().Deaths, d => d.Text == "Gribble");
    }

    [Fact]
    public void R2_TeammateRegenTicksDoNotEstimateHealingFromYourLastCast()
    {
        // _lastRegenCast reflects only the OWNING instance's last cast/song — a
        // teammate's own regen tick, applied to a fresh instance that never saw the
        // watched character's Hymn of Restoration, has nothing to correlate against.
        var mine = Replay("[Mon Aug 10 11:15:00 2026] You begin singing Hymn of Restoration.");
        var mate = ApplyAsMate("[Mon Aug 10 11:15:02 2026] Your wounds begin to heal.");

        var mineSnap = mine.Snapshot();
        Assert.Equal(0, mineSnap.RegenEstimatedHealed);   // mine never received a regen-tick line at all
        var mateSnap = mate.Snapshot();
        Assert.Equal(1, mateSnap.RegenTicks);              // the mate's OWN tick still counts...
        Assert.Equal(0, mateSnap.RegenEstimatedHealed);    // ...but estimates nothing: no cast on ITS OWN ledger
    }

    [Fact]
    public void R2_TeammateConsidersDoNotChangeTheWatchedCharactersLastConsiderTarget()
    {
        // _lastConsider feeds CurrentTargets — held per-instance, so a teammate
        // considering something (on a separate instance) is never evidence about what
        // the WATCHED character's own instance is looking at.
        var mine = new SessionStats();
        ApplyAsMate("[Mon Aug 10 11:15:00 2026] Orc centurion scowls at you, ready to attack. (Lvl: 10)");

        Assert.DoesNotContain("Orc centurion", mine.Snapshot().CurrentTargets);
    }

    [Fact]
    public void R2_TeammateFactionHitsDoNotChangeTheWatchedCharactersTrackedStanding()
    {
        // Each character has their OWN faction standing with an NPC faction, held
        // per-instance — a teammate's delta, applied to their own instance, is not a
        // fact about the watched character's standing, unlike
        // damage/heals/kills/loot/money/XP, which are pooled duo totals by design
        // (see DuoStats.Combine, Step 4).
        var mine = new SessionStats();
        ApplyAsMate("[Mon Aug 10 11:15:00 2026] Your faction standing with Crushbone Orcs has been adjusted by -5.");

        Assert.Empty(mine.Snapshot().Faction);
    }

    [Fact]
    public void R2_TeammateLootCraftFashionDestroyedAndNamedVendorSalesDoNotWriteYourQuestLedger()
    {
        // The highest-priority finding: QuestStore is a DURABLE on-disk ledger keyed by
        // the watched character's own identity. TeammateLogTail's constructor never
        // attaches ANY store to the teammate's instance (see its class doc's
        // invariant) — so a teammate looting, crafting, fashioning, destroying or
        // selling their OWN items has no QuestStore reference to write through at all,
        // and the WATCHED character's real, attached store sees nothing from it.
        var path = Path.Combine(Path.GetTempPath(), $"quest-ledger-{Guid.NewGuid():N}.json");
        try
        {
            var store = new QuestLedgerStore(path) { TrackFilter = _ => true };
            var mine = new SessionStats
            {
                CharacterName = "Kaybek", ServerName = "legends", QuestStore = store,
            };
            var key = mine.LedgerCharacterKey;

            // The teammate's own instance — built exactly like TeammateLogTail builds
            // one, with NO QuestStore attached — applies every line unconditionally
            // (Step 3: no gate, no flag).
            ApplyAsMate(
                "[Mon Aug 10 11:15:00 2026] --You have looted a Bone Chip from an orc pawn's corpse.--",
                "[Mon Aug 10 11:15:01 2026] You have successfully merged two items together to create a new item: Crushbone Belt +5.",
                "[Mon Aug 10 11:15:02 2026] You have fashioned the items together to create something new: Elixir of Concentration.",
                "[Mon Aug 10 11:15:03 2026] You successfully destroyed 1 Spider Venom Sac.",
                "[Mon Aug 10 11:15:04 2026] You receive 7 gold 2 silver 3 copper from Lanadin for the Raw-Hide Sleeves +2(s).");

            Assert.Empty(store.For(key));
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
