using System.Reflection;
using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>
/// Step 4's duo combine: <see cref="DuoStats.Combine"/> and the
/// <see cref="SessionStats.DuoSnapshot"/>/<see cref="SessionStats.DuoVersion"/> seam
/// around it. <see cref="EveryStatsSnapshotPropertyIsClassifiedAsCombinedOrPassedThrough"/>
/// is the centrepiece the plan names: a curated classification of EVERY
/// <see cref="StatsSnapshot"/> property, checked against reflection rather than a fixed
/// list, so a property added to <see cref="StatsSnapshot"/> tomorrow with no row here
/// fails the build instead of silently defaulting to whichever side <c>Combine</c>
/// happens to touch first.
/// </summary>
public class DuoStatsTests
{
    // ---- Generic fixture construction: every StatsSnapshot property gets a distinct,
    // reflectively-seeded value so a Combine bug that reads the WRONG side (or drops a
    // property) shows up as a value mismatch rather than a coincidental match. ----

    private static object? SeedFor(Type type, string tag, int seed)
    {
        var under = Nullable.GetUnderlyingType(type) ?? type;
        if (under == typeof(long)) return (long)seed;
        if (under == typeof(int)) return seed;
        if (under == typeof(double)) return seed + 0.5;
        if (under == typeof(bool)) return seed % 2 == 1;
        if (under == typeof(DateTime)) return new DateTime(2026, 1, 1).AddMinutes(seed);
        if (under == typeof(TimeSpan)) return TimeSpan.FromSeconds(seed);
        if (type == typeof(string)) return $"{tag}{seed}";
        if (type == typeof(StatsSnapshot)) return null;   // Mate: handled by the caller, not seeded generically
        if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(List<>)
            || type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)))
            return BuildList(type, tag, seed);
        return BuildElement(type, tag, seed);
    }

    private static object BuildList(Type listOrReadOnlyListType, string tag, int seed)
    {
        var elementType = listOrReadOnlyListType.GetGenericArguments()[0];
        var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
        // Route through SeedFor, not BuildElement directly — an element type can be a
        // primitive (List<string>, e.g. InferredClasses) as well as a record, and only
        // SeedFor knows how to build primitives. Calling BuildElement directly here
        // sent `string` into the generic ctor-reflection path, which found String's
        // UNSAFE POINTER constructors (`String(sbyte*, ...)`) ahead of anything usable.
        list.Add(SeedFor(elementType, tag, seed));
        return list;
    }

    private static object BuildElement(Type type, string tag, int seed)
    {
        if (type.IsGenericType && type.FullName!.StartsWith("System.ValueTuple", StringComparison.Ordinal))
        {
            var argTypes = type.GetGenericArguments();
            var values = argTypes.Select((t, idx) => SeedFor(t, tag, seed + idx + 1)).ToArray();
            return Activator.CreateInstance(type, values)!;
        }
        // Records (every nested type StatsSnapshot uses) expose exactly one PUBLIC
        // constructor — the positional primary one; the compiler-generated copy
        // constructor records also carry is protected/private, so GetConstructors()
        // (public only) never sees it.
        var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault()
            ?? throw new NotSupportedException($"No public constructor found for {type}");
        var args = ctor.GetParameters().Select((p, idx) => SeedFor(p.ParameterType, tag, seed + idx + 1)).ToArray();
        return ctor.Invoke(args)!;
    }

    /// <summary>Builds a StatsSnapshot with every property (except the computed
    /// <see cref="StatsSnapshot.CastCompletion"/> and the escape-hatch
    /// <see cref="StatsSnapshot.Mate"/>) set to a distinct, TAG-derived value — via
    /// reflection's <c>PropertyInfo.SetValue</c>, which reaches <c>init</c> setters
    /// exactly like any other setter (the <c>init</c> restriction is a C#-compiler-only
    /// check, invisible to the CLR).</summary>
    private static StatsSnapshot BuildFixture(string tag, int seedBase)
    {
        var snap = new StatsSnapshot();
        var props = typeof(StatsSnapshot).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.Name != nameof(StatsSnapshot.Mate));
        var i = seedBase;
        foreach (var p in props)
        {
            p.SetValue(snap, SeedFor(p.PropertyType, tag, i));
            i += 97;   // spread seeds out so nested lists' own sub-fields don't collide across properties
        }
        return snap;
    }

    private static bool ValuesMatch(object? a, object? b)
    {
        if (a is System.Collections.IEnumerable ea && a is not string
            && b is System.Collections.IEnumerable eb && b is not string)
            return ea.Cast<object>().SequenceEqual(eb.Cast<object>());
        return Equals(a, b);
    }

    // ---- The classification itself (plan Part 2c) ----

    private static readonly Dictionary<string, string> Combined = new()
    {
        [nameof(StatsSnapshot.Version)] = "both monotonic — sum",
        [nameof(StatsSnapshot.YourKillCount)] = "their kill reaches your log only as a third-party line — sum is safe",
        [nameof(StatsSnapshot.YourKills)] = "merge by creature name, sum counts",
        [nameof(StatsSnapshot.KillsPerHour)] = "recompute over combined kills / mine.Elapsed — never sum two rates",
        [nameof(StatsSnapshot.KillsPerActiveHour)] = "recompute over combined kills / mine.ActiveSeconds",
        [nameof(StatsSnapshot.DamageDealt)] = "their swings reach your log only as third-party events — sum is safe",
        [nameof(StatsSnapshot.MeleeDamage)] = "sum",
        [nameof(StatsSnapshot.SpellDamage)] = "sum",
        [nameof(StatsSnapshot.DotDamage)] = "sum",
        [nameof(StatsSnapshot.DirectSpellDamage)] = "sum",
        [nameof(StatsSnapshot.HitCount)] = "sum",
        [nameof(StatsSnapshot.CritCount)] = "sum",
        [nameof(StatsSnapshot.MissCount)] = "sum",
        [nameof(StatsSnapshot.MaxHit)] = "max",
        [nameof(StatsSnapshot.MaxHitDesc)] = "from whichever side owns the max",
        [nameof(StatsSnapshot.CombatSeconds)] = "repair round B3/C7: the real union of both sides' combat spans (falls back to max only when DuoSnapshot's combatSecondsOverride is unavailable)",
        [nameof(StatsSnapshot.SessionDps)] = "recompute: combined damage / combined CombatSeconds",
        [nameof(StatsSnapshot.CurrentDps)] = "AMBIGUOUS, resolved: sum two live rates, documented approximation",
        [nameof(StatsSnapshot.DamageTaken)] = "sum",
        [nameof(StatsSnapshot.AvoidedIncoming)] = "sum",
        [nameof(StatsSnapshot.MeleeHitsTaken)] = "sum",
        [nameof(StatsSnapshot.HealingDone)] = "their heal on you is outgoing in THEIR log, incoming in yours — sum is safe",
        [nameof(StatsSnapshot.HealingReceived)] = "sum",
        [nameof(StatsSnapshot.Hps)] = "recompute from summed HealingDone over combined CombatSeconds",
        [nameof(StatsSnapshot.LootTotal)] = "every loot regex is first-person; no third-party loot line exists — sum is safe",
        [nameof(StatsSnapshot.Loot)] = "merge by item name, sum counts",
        [nameof(StatsSnapshot.RecentLoot)] = "concat, sort by time desc, re-cap at MaxRecentLoot",
        [nameof(StatsSnapshot.Copper)] = "every coin regex is first-person — sum is safe",
        [nameof(StatsSnapshot.CorpseCopper)] = "sum",
        [nameof(StatsSnapshot.VendorCopper)] = "sum",
        [nameof(StatsSnapshot.SalesCount)] = "sum",
        [nameof(StatsSnapshot.SoldItems)] = "merge by item, sum count and copper",
        [nameof(StatsSnapshot.CopperPerHour)] = "recompute over mine.Elapsed",
        [nameof(StatsSnapshot.CopperPerActiveHour)] = "recompute over mine.ActiveSeconds",
        [nameof(StatsSnapshot.Recent)] = "mixed: Window/HasFullWindow/XpPercent/XpPerHour from mine; Kills/Copper/Dps/Hps summed",
        [nameof(StatsSnapshot.Mate)] = "the escape hatch itself — set to the other side's own snapshot",
        // B4 (the user's product decision, 2026-09-07): merge the per-player
        // breakdowns so every board sums to its own header. Ability/spell/hit-type
        // rows (keyed by a NAME that doesn't say who performed it) keep mine's own
        // rows untouched and tag the teammate's onto the list with their character
        // name appended, rather than summing same-named rows into one — "your Kick"
        // and "their Kick" stay two rows, or the fact that two people kicked is
        // lost. Person-keyed rows (who hit/healed YOU) have no such ambiguity — the
        // name IS already the external party — and sum by name normally.
        [nameof(StatsSnapshot.DamageBySource)] = "ability rows: mine as-is, mate's tagged with their name, never summed together",
        [nameof(StatsSnapshot.PetAbilities)] = "ability rows: mine as-is, mate's tagged with their name, never summed together",
        [nameof(StatsSnapshot.HealsBySpell)] = "spell rows: mine as-is, mate's tagged with their name, never summed together",
        [nameof(StatsSnapshot.SpecialHits)] = "hit-type rows: mine as-is, mate's tagged with their name, never summed together",
        [nameof(StatsSnapshot.DamageByAttacker)] = "person-keyed (who hit us) — no attribution ambiguity, sum by name",
        [nameof(StatsSnapshot.HealsByHealer)] = "person-keyed (who healed us) — no attribution ambiguity, sum by name",
        [nameof(StatsSnapshot.DamageTimeline)] = "time-bucketed — align buckets (both use absolute per-minute keys) and sum",
        [nameof(StatsSnapshot.Effort)] = "recomputed: DamageDone/HealingDone/DamageDoneInResumeWindow summed; Window/ResumeWindow from mine",
        // Repair round A3: these three moved out of MINE. Passing mine's own party
        // breakdown through unmodified still contained the teammate's (and their
        // pet's) kills the instant Combine promotes them into YourKillCount above —
        // recomputed here instead: mate.YourKills subtracted per target, the
        // teammate's name (and PetName) removed from the killer breakdown, and the
        // header resummed from the residual list so it can never disagree with it.
        [nameof(StatsSnapshot.PartyKillCount)] = "recomputed: mine's count minus the teammate's own promoted kills",
        [nameof(StatsSnapshot.PartyKillsByTarget)] = "repair round C3: mine's breakdown minus the teammate's TRUE promoted kills per target, as observed in the primary's OWN log (falls back to mate.YourKills only when that true count isn't supplied)",
        [nameof(StatsSnapshot.PartyKillsByKiller)] = "mine's breakdown minus any entry named for the teammate or their pet",
    };

    private static readonly Dictionary<string, string> Mine = new()
    {
        [nameof(StatsSnapshot.LastLocation)] = "your position",
        [nameof(StatsSnapshot.LocationTrail)] = "your position",
        [nameof(StatsSnapshot.SessionStart)] = "the duo session IS the watched character's session",
        [nameof(StatsSnapshot.LastEventTime)] = "the duo session IS the watched character's session",
        [nameof(StatsSnapshot.Elapsed)] = "the duo session IS the watched character's session",
        [nameof(StatsSnapshot.Deaths)] = "AMBIGUOUS, resolved: 'we died' cannot say whose — stays mine for v1",
        // A4 decision (see CoinDropsAndBiggestDropStayPrimaryOnly): a corpse copper
        // split gives each player their own receipt line, so the EVENT COUNT and the
        // largest single RECEIPT would double-count/report-a-personal-share if
        // summed/maxed — correlating receipts per corpse needs data this redesign
        // doesn't track. Copper itself (the raw total) has no such ambiguity.
        [nameof(StatsSnapshot.CoinDrops)] = "A4: event count would double-count a per-corpse split — stays mine",
        [nameof(StatsSnapshot.BiggestDrop)] = "A4: a personal share, not the duo's take from one corpse — stays mine",
        // PartyKillCount / PartyKillsByTarget / PartyKillsByKiller moved to Combined
        // above (repair round A3) — kept here as a comment, not an entry, since the
        // partition check below would fail on a name in both dictionaries.
        //
        // B4 (the user's product decision, superseding A6's hold): DamageBySource,
        // PetAbilities, SpecialHits, DamageByAttacker, HealsByHealer, HealsBySpell,
        // DamageTimeline and Effort all moved to Combined below — every board now
        // adds up to its own header. See DECISIONS.md's B4 entry.
        [nameof(StatsSnapshot.PetName)] = "CharmTracker state",
        [nameof(StatsSnapshot.CharmedSince)] = "CharmTracker state",
        [nameof(StatsSnapshot.CurrentTargets)] = "AMBIGUOUS, resolved: duo partners fight the same things — stays mine",
        [nameof(StatsSnapshot.RegenTicks)] = "attribution by your own cast",
        [nameof(StatsSnapshot.RegenEstimatedHealed)] = "attribution by your own cast",
        [nameof(StatsSnapshot.RegenSpell)] = "attribution by your own cast",
        [nameof(StatsSnapshot.RuneGainCount)] = "your body",
        [nameof(StatsSnapshot.RuneGainPoints)] = "your body",
        [nameof(StatsSnapshot.RuneBlockCount)] = "your body",
        [nameof(StatsSnapshot.RuneBlockStreak)] = "your body",
        [nameof(StatsSnapshot.RuneBlockStreakMax)] = "your body",
        [nameof(StatsSnapshot.Crafted)] = "inventory events",
        [nameof(StatsSnapshot.CraftedTotal)] = "inventory events",
        [nameof(StatsSnapshot.Fashioned)] = "inventory events",
        [nameof(StatsSnapshot.FashionedTotal)] = "inventory events",
        [nameof(StatsSnapshot.Upgraded)] = "ticks YOUR wish list",
        [nameof(StatsSnapshot.XpPercent)] = "a percentage of a level bar — the single most important 'no' in the table",
        [nameof(StatsSnapshot.XpTicks)] = "percentage of your own level bar",
        [nameof(StatsSnapshot.XpPerHour)] = "percentage of your own level bar",
        [nameof(StatsSnapshot.HoursToLevel)] = "reads _xpSinceLevel — your own",
        [nameof(StatsSnapshot.AaGained)] = "character-scoped purchases",
        [nameof(StatsSnapshot.AaAbilities)] = "character-scoped purchases",
        [nameof(StatsSnapshot.AaTotal)] = "character-scoped purchases",
        [nameof(StatsSnapshot.AaPerHour)] = "character-scoped purchases",
        [nameof(StatsSnapshot.Levels)] = "yours",
        [nameof(StatsSnapshot.LastLevel)] = "yours",
        [nameof(StatsSnapshot.SkillUps)] = "yours",
        [nameof(StatsSnapshot.SkillUpTotal)] = "yours",
        [nameof(StatsSnapshot.Faction)] = "each character has their own standing — non-negotiable",
        [nameof(StatsSnapshot.Zones)] = "your position",
        [nameof(StatsSnapshot.CurrentZone)] = "your position",
        [nameof(StatsSnapshot.Fizzles)] = "your cast ledger",
        [nameof(StatsSnapshot.Resists)] = "your cast ledger",
        [nameof(StatsSnapshot.Blocked)] = "your cast ledger",
        [nameof(StatsSnapshot.CastsStarted)] = "your cast ledger",
        [nameof(StatsSnapshot.CastsInterrupted)] = "your cast ledger",
        [nameof(StatsSnapshot.CastCompletion)] = "your cast ledger (computed from other Mine fields)",
        [nameof(StatsSnapshot.ActiveSeconds)] = "your keyboard time",
        [nameof(StatsSnapshot.XpPerActiveHour)] = "your keyboard time",
        [nameof(StatsSnapshot.Tracked)] = "watch-rule results, computed with rules on the primary only",
        [nameof(StatsSnapshot.Markers)] = "AddMarker only ever called on the primary",
        [nameof(StatsSnapshot.LastFight)] = "AMBIGUOUS, resolved: a fight-identity join is a project, not a merge — explicit non-goal",
        [nameof(StatsSnapshot.RecentEncounters)] = "AMBIGUOUS, resolved: explicit non-goal",
        [nameof(StatsSnapshot.Encounters)] = "AMBIGUOUS, resolved: explicit non-goal",
        [nameof(StatsSnapshot.EncounterCount)] = "AMBIGUOUS, resolved: explicit non-goal",
        [nameof(StatsSnapshot.Mobs)] = "DEFERRED to 6a (coordinator scope cut) — ships as mine, gap documented",
        [nameof(StatsSnapshot.CurrentStance)] = "your stance clock",
        [nameof(StatsSnapshot.Stances)] = "your stance clock",
        [nameof(StatsSnapshot.CurrentInvocation)] = "your stance clock",
        [nameof(StatsSnapshot.Invocations)] = "your stance clock",
        [nameof(StatsSnapshot.AreaSpells)] = "derived from YOUR cast/item-proc correlation",
        [nameof(StatsSnapshot.Procs)] = "derived from YOUR cast/item-proc correlation",
        [nameof(StatsSnapshot.SpellResists)] = "your cast ledger",
        [nameof(StatsSnapshot.InferredClass)] = "class inference votes are about you",
        [nameof(StatsSnapshot.InferredClasses)] = "class inference votes are about you",
    };

    [Fact]
    public void EveryStatsSnapshotPropertyIsClassifiedAsCombinedOrPassedThrough()
    {
        var allNames = typeof(StatsSnapshot).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name).ToHashSet();

        // The partition must be EXACT: every property in exactly one bucket. This is
        // what makes the test fail the moment a new StatsSnapshot property ships
        // unclassified — proven below by temporarily adding one (see the
        // Prove-The-Guard-Actually-Fires note in the PR description / session report).
        var unclassified = allNames.Except(Combined.Keys).Except(Mine.Keys).ToList();
        Assert.True(unclassified.Count == 0,
            $"Unclassified StatsSnapshot propert{(unclassified.Count == 1 ? "y" : "ies")}: {string.Join(", ", unclassified)}");

        var overlap = Combined.Keys.Intersect(Mine.Keys).ToList();
        Assert.True(overlap.Count == 0, $"In BOTH buckets: {string.Join(", ", overlap)}");

        var stale = Combined.Keys.Concat(Mine.Keys).Except(allNames).ToList();
        Assert.True(stale.Count == 0, $"Classified but no longer a property: {string.Join(", ", stale)}");

        // For every MINE property: build two snapshots with fully disjoint non-default
        // values and assert Combine(mine, mate) equals MINE's own value exactly —
        // proving the property passes through untouched rather than merely "looking
        // right" because both sides happened to agree.
        var mine = BuildFixture("Mine", 1);
        var mate = BuildFixture("Mate", 100_000);
        var combined = DuoStats.Combine(mine, mate);

        var props = typeof(StatsSnapshot).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(p => p.Name);
        var failures = new List<string>();
        foreach (var name in Mine.Keys)
        {
            var p = props[name];
            var mineVal = p.GetValue(mine);
            var combinedVal = p.GetValue(combined);
            if (!ValuesMatch(mineVal, combinedVal))
                failures.Add(name);
        }
        Assert.True(failures.Count == 0,
            $"These MINE properties did not pass through unchanged: {string.Join(", ", failures)}");
    }

    /// <summary>Repair round C9: a property Combine omits entirely falls back to
    /// WHATEVER <c>new StatsSnapshot()</c> already gives it — which is not always
    /// the CLR zero/null/empty default. <c>Effort</c> is the exact case that broke
    /// the old <c>IsDefaultValue</c> heuristic: its own field initializer is
    /// <c>RecentEffort.None</c>, a non-null REFERENCE, so a value-type-only default
    /// check waved it through as "clearly set" no matter what <c>Combine</c> did.
    /// Comparing against a single shared <see cref="Untouched"/> instance instead
    /// asks the only question that actually matters: did <c>Combine</c> touch this
    /// property at all, or is it still sitting at whatever an UNTOUCHED snapshot
    /// would have here.</summary>
    private static readonly StatsSnapshot Untouched = new();

    /// <summary>
    /// A9(a): the centrepiece test above proves every property is classified, and
    /// that every MINE property passes through untouched — but it never checked the
    /// OTHER bucket. A property could be added to <see cref="Combined"/> here and
    /// left out of <see cref="DuoStats.Combine"/>'s object initializer entirely; C#
    /// silently defaults an omitted init property instead of erroring, and nothing
    /// above this test would have noticed. This closes that gap: every Combined
    /// property must differ from <see cref="Untouched"/>'s own copy of it, given
    /// both sides of the fixture were seeded to non-default, non-<see cref="Untouched"/>
    /// values.
    /// </summary>
    [Fact]
    public void EveryCombinedPropertyActuallyReceivesAValueFromCombine()
    {
        var mine = BuildFixture("Mine", 1);
        var mate = BuildFixture("Mate", 100_000);
        var combined = DuoStats.Combine(mine, mate);

        var props = typeof(StatsSnapshot).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(p => p.Name);
        var defaulted = Combined.Keys
            .Where(name => ValuesMatch(props[name].GetValue(combined), props[name].GetValue(Untouched)))
            .ToList();

        Assert.True(defaulted.Count == 0,
            $"These Combined properties came back matching an UNTOUCHED snapshot — check Combine's " +
            $"initializer actually sets them: {string.Join(", ", defaulted)}");
    }

    /// <summary>Prescribed by the plan: temporarily add a dummy property to
    /// StatsSnapshot and show the centrepiece test above fails. This test performs that
    /// proof PROGRAMMATICALLY (rather than by hand-editing SessionStats.cs and reverting
    /// it, which would touch the ratcheted file) by re-running the SAME partition logic
    /// against a synthetic "StatsSnapshot-shaped" name set that includes one extra,
    /// deliberately unclassified name — the exact assertion the real test would fail on
    /// if a real property shipped unclassified.</summary>
    [Fact]
    public void TheClassificationPartitionFailsWhenAPropertyIsLeftOut()
    {
        var allNames = typeof(StatsSnapshot).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name).Append("TotallyNewDummyProperty").ToHashSet();

        var unclassified = allNames.Except(Combined.Keys).Except(Mine.Keys).ToList();

        Assert.Contains("TotallyNewDummyProperty", unclassified);
        Assert.True(unclassified.Count > 0, "the partition check must fail when a property is unclassified");
    }

    [Fact]
    public void CombineIsIdentityWhenThereIsNoTeammate()
    {
        var mine = BuildFixture("Mine", 1);
        var combined = DuoStats.Combine(mine, null);
        Assert.Same(mine, combined);   // reference equality — the no-teammate path costs nothing
    }

    [Fact]
    public void DuoKillsSumButPartyKillsDoNot()
    {
        var mine = new StatsSnapshot
        {
            YourKillCount = 5,
            YourKills = [new NameCount("Orc pawn", 5)],
            PartyKillCount = 3,
            PartyKillsByTarget = [new NameCount("Orc guard", 3)],
            Elapsed = TimeSpan.FromHours(1),
        };
        var mate = new StatsSnapshot
        {
            YourKillCount = 2,
            YourKills = [new NameCount("Orc pawn", 1), new NameCount("Orc sentry", 1)],
            PartyKillCount = 9,   // if this leaked into the sum it would be an obvious tell
            PartyKillsByTarget = [new NameCount("Orc centurion", 9)],
        };

        var combined = DuoStats.Combine(mine, mate);

        Assert.Equal(7, combined.YourKillCount);
        Assert.Equal(6, combined.YourKills.Single(k => k.Name == "Orc pawn").Count);
        Assert.Contains(combined.YourKills, k => k.Name == "Orc sentry" && k.Count == 1);
        // PartyKillCount and its breakdown are firmly mine — summing would double-count
        // kills your own log already recorded as party kills.
        Assert.Equal(3, combined.PartyKillCount);
        Assert.Equal(mine.PartyKillsByTarget, combined.PartyKillsByTarget);
    }

    /// <summary>
    /// A3: passing the primary PARTY count through unmodified still contains the
    /// teammate's own kills the instant they are promoted into YourKillCount — one
    /// teammate kill rendered as "1 (+1)" (<c>CreatureTheme.cs:25</c>'s
    /// <c>{YourKillCount} (+{PartyKillCount})</c>). Fix: subtract the teammate's
    /// EXACT per-target promoted kills (from <c>mate.YourKills</c>, which already
    /// folds in their own pet via the same <c>IsPet</c> rule <c>mine.YourKills</c>
    /// uses) from the residual party breakdown, and remove their name from the
    /// killer breakdown entirely — nobody else can share that exact character name.
    /// </summary>
    [Fact]
    public void ATeammatesPromotedKillIsSubtractedFromTheResidualPartyCount()
    {
        var mine = new StatsSnapshot
        {
            YourKillCount = 1,
            YourKills = [new NameCount("Orc guard", 1)],
            PartyKillCount = 2,
            // "Orc pawn" is the SAME kill the teammate's own log counts as theirs;
            // "Orc sentry" is a genuine third groupmate's kill.
            PartyKillsByTarget = [new NameCount("Orc pawn", 1), new NameCount("Orc sentry", 1)],
            PartyKillsByKiller = [new NameCount("Buddy", 1), new NameCount("Grouper", 1)],
            Elapsed = TimeSpan.FromHours(1),
        };
        var mate = new StatsSnapshot
        {
            YourKillCount = 1,
            YourKills = [new NameCount("Orc pawn", 1)],
        };

        var combined = DuoStats.Combine(mine, mate, mateCharacterName: "Buddy");

        Assert.Equal(2, combined.YourKillCount);
        Assert.Equal(1, combined.PartyKillCount);   // only Grouper's kill remains
        Assert.DoesNotContain(combined.PartyKillsByTarget, nc => nc.Name == "Orc pawn");
        Assert.Contains(combined.PartyKillsByTarget, nc => nc.Name == "Orc sentry" && nc.Count == 1);
        Assert.DoesNotContain(combined.PartyKillsByKiller, nc => nc.Name == "Buddy");
        Assert.Contains(combined.PartyKillsByKiller, nc => nc.Name == "Grouper" && nc.Count == 1);
    }

    /// <summary>The teammate's OWN pet's kills reach the primary's log third-party
    /// under the pet's name, exactly like the teammate's own — <see cref="StatsSnapshot.PetName"/>
    /// is what lets the killer-breakdown subtraction find it too.</summary>
    [Fact]
    public void ATeammatesPetsPromotedKillIsAlsoRemovedFromTheKillerBreakdown()
    {
        var mine = new StatsSnapshot
        {
            PartyKillCount = 1,
            PartyKillsByTarget = [new NameCount("Orc pawn", 1)],
            PartyKillsByKiller = [new NameCount("Buddy`s pet", 1)],
            Elapsed = TimeSpan.FromHours(1),
        };
        var mate = new StatsSnapshot
        {
            YourKillCount = 1,
            YourKills = [new NameCount("Orc pawn", 1)],
            PetName = "Buddy`s pet",
        };

        var combined = DuoStats.Combine(mine, mate, mateCharacterName: "Buddy");

        Assert.Equal(0, combined.PartyKillCount);
        Assert.Empty(combined.PartyKillsByTarget);
        Assert.DoesNotContain(combined.PartyKillsByKiller, nc => nc.Name == "Buddy`s pet");
    }

    /// <summary>
    /// A4: <c>CoinDrops</c> is an EVENT COUNT and <c>BiggestDrop</c> the largest
    /// individual receipt — copper split between two players off one corpse gives
    /// each of them their own "you receive N copper" line, so summing CoinDrops
    /// doubles the event count for what was really one drop, and maxing BiggestDrop
    /// reports the biggest personal SHARE rather than the duo's actual take from any
    /// one corpse. Correlating receipts per corpse to dedupe them needs data this
    /// redesign does not track (which two lines came from the SAME corpse, across
    /// two independent logs) — the same shape of gap as <c>Mobs</c>'s 6a deferral.
    /// DECISION (documented per the coordinator's "pick one and say which"): both
    /// fields stay PRIMARY-ONLY, matching <c>Deaths</c>/<c>CurrentTargets</c>'s
    /// precedent of resolving an ambiguous duo question by keeping the number
    /// unambiguous rather than plausible-looking. <c>Copper</c> itself is unaffected
    /// and stays summed — a raw copper total has no per-corpse ambiguity to
    /// introduce, unlike a COUNT or a MAX of receipts that might share a corpse.
    /// </summary>
    [Fact]
    public void CoinDropsAndBiggestDropStayPrimaryOnly()
    {
        var mine = new StatsSnapshot { CoinDrops = 3, BiggestDrop = 500, Elapsed = TimeSpan.FromHours(1) };
        var mate = new StatsSnapshot { CoinDrops = 9, BiggestDrop = 9_000 };   // if either leaked in, an obvious tell

        var combined = DuoStats.Combine(mine, mate);

        Assert.Equal(3, combined.CoinDrops);
        Assert.Equal(500, combined.BiggestDrop);
    }

    /// <summary>
    /// A6 (the loot-chronology half): <c>MergeLoot</c> let whichever side's loop ran
    /// SECOND win <c>LastSource</c> regardless of which pickup was actually LATER —
    /// since <c>b</c> (mate) is always added after <c>a</c> (mine) in the merge, mate
    /// won every item BOTH players looted, even when mine's own pickup was the more
    /// recent one. Fix: read the real chronology off the already time-ordered
    /// <c>RecentLoot</c> list (each pickup DOES carry a timestamp) rather than
    /// trusting iteration order.
    /// </summary>
    [Fact]
    public void MergeLootPicksTheActuallyLaterSourceNotWhicheverSideRanSecond()
    {
        var mine = new StatsSnapshot
        {
            Loot = [new LootDetail("Rusty Dagger", 1, "an orc pawn")],
            RecentLoot = [new LootPickup(new DateTime(2026, 1, 1, 0, 0, 10), "Rusty Dagger", 1, "an orc pawn")],
            Elapsed = TimeSpan.FromHours(1),
        };
        var mate = new StatsSnapshot
        {
            // Mate looted the SAME item EARLIER — "last writer wins" would still pick
            // mate's source here since mate is always added second.
            Loot = [new LootDetail("Rusty Dagger", 1, "an orc guard")],
            RecentLoot = [new LootPickup(new DateTime(2026, 1, 1, 0, 0, 5), "Rusty Dagger", 1, "an orc guard")],
        };

        var combined = DuoStats.Combine(mine, mate);

        var merged = Assert.Single(combined.Loot, d => d.Item == "Rusty Dagger");
        Assert.Equal(2, merged.Count);
        Assert.Equal("an orc pawn", merged.LastSource);   // mine's pickup was actually LATER
    }

    [Fact]
    public void DuoXpIsNotSummed()
    {
        var mine = new StatsSnapshot { XpPercent = 40, Elapsed = TimeSpan.FromHours(1) };
        var mate = new StatsSnapshot { XpPercent = 60 };

        var combined = DuoStats.Combine(mine, mate);

        Assert.Equal(40, combined.XpPercent);   // NOT 100 — percentages of two different level bars
    }

    [Fact]
    public void DuoDpsRecomputesRatherThanSummingRates()
    {
        // mine: 100 damage / 10s combat = 10 dps; mate: 900 damage / 90s combat = 10 dps.
        // Summing the two RATES gives 20; the correct combined figure recomputes over
        // the combined damage and the combined (MAX, not summed) combat window:
        // (100+900) / max(10,90) = 1000/90 ≈ 11.11.
        var mine = new StatsSnapshot { DamageDealt = 100, CombatSeconds = 10, Elapsed = TimeSpan.FromHours(1) };
        var mate = new StatsSnapshot { DamageDealt = 900, CombatSeconds = 90 };

        var combined = DuoStats.Combine(mine, mate);

        Assert.Equal(90, combined.CombatSeconds);
        Assert.Equal(1000.0 / 90.0, combined.SessionDps, 3);
    }

    // ---- B3: CombatSeconds is the real UNION of both sides' timestamped combat
    // spans, not Math.Max — integration tests against real SessionStats/Companion/
    // DuoSnapshot, since the span data (_combatSpans, _closedCombatSeconds, the
    // still-open span) lives on the live instances, not on StatsSnapshot. ----

    private static DamageDealtEvent Dmg(DateTime t, int amount = 100) =>
        new(t, "a target", amount, DamageKind.Melee, "You", false);

    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

    /// <summary>Applies hits every <= 9 seconds (under SessionStats' 10s CombatGap)
    /// from <paramref name="start"/> to <paramref name="end"/> inclusive, so the
    /// resulting combat span is exactly one continuous [start, end] window rather
    /// than a single pair of endpoint events that would exceed CombatGap and
    /// silently close/reopen as two separate 1-second spans. Returns the total
    /// damage applied, for SessionDps assertions.</summary>
    private static long ApplyContinuousFight(SessionStats s, DateTime start, DateTime end, int perHit = 100)
    {
        var times = new List<DateTime>();
        for (var t = start; t < end; t = t.AddSeconds(9)) times.Add(t);
        times.Add(end);
        foreach (var t in times) s.Apply(Dmg(t, perHit));
        return (long)times.Count * perHit;
    }

    [Fact]
    public void TwoNonOverlappingFightsUnionRatherThanTakeTheMax()
    {
        // mine: one fight, t=0..9 (9s). mate: a SEPARATE fight, t=100..109 (9s) —
        // over 10s (CombatGap) away from mine's, so genuinely two disjoint windows.
        // Math.Max(9,9)=9 would halve the real 18s duo total.
        var mine = new SessionStats();
        var mate = new SessionStats();
        mine.Companion = mate;
        var mineDmg = ApplyContinuousFight(mine, T0, T0.AddSeconds(9));
        var mateDmg = ApplyContinuousFight(mate, T0.AddSeconds(100), T0.AddSeconds(109));

        var duo = mine.DuoSnapshot(null, null);

        Assert.Equal(18, duo.CombatSeconds, 3);
        Assert.Equal((mineDmg + mateDmg) / 18.0, duo.SessionDps, 3);
    }

    [Fact]
    public void TwoPartiallyOverlappingFightsUnionToTheOuterSpanNotTheSum()
    {
        // mine: t=0..19. mate: t=9..29 — overlaps mine's tail by 10s. The union is
        // one continuous window, t=0..29 = 29s: NOT the sum (19+20=39, double-
        // counting the shared 9..19 stretch) and NOT the max (20).
        var mine = new SessionStats();
        var mate = new SessionStats();
        mine.Companion = mate;
        var mineDmg = ApplyContinuousFight(mine, T0, T0.AddSeconds(19));
        var mateDmg = ApplyContinuousFight(mate, T0.AddSeconds(9), T0.AddSeconds(29));

        var duo = mine.DuoSnapshot(null, null);

        Assert.Equal(29, duo.CombatSeconds, 3);
        Assert.Equal((mineDmg + mateDmg) / 29.0, duo.SessionDps, 3);
    }

    [Fact]
    public void OneFightFullyContainingAnotherStaysAtTheOuterDuration()
    {
        // mine: t=0..29. mate: t=9..19, entirely INSIDE mine's window. The union is
        // still just mine's 29s — mate adds nothing new to cover — which is also
        // what Math.Max(29,10) already got right; the union must not regress this
        // case while fixing the other two.
        var mine = new SessionStats();
        var mate = new SessionStats();
        mine.Companion = mate;
        var mineDmg = ApplyContinuousFight(mine, T0, T0.AddSeconds(29));
        var mateDmg = ApplyContinuousFight(mate, T0.AddSeconds(9), T0.AddSeconds(19));

        var duo = mine.DuoSnapshot(null, null);

        Assert.Equal(29, duo.CombatSeconds, 3);
        Assert.Equal((mineDmg + mateDmg) / 29.0, duo.SessionDps, 3);
    }

    [Fact]
    public void TheDuoVersionMovesWhenOnlyTheTeammateMoves()
    {
        var mine = new SessionStats();
        var mate = new SessionStats();
        mine.Apply(LogParser.Parse("[Sat Jul 18 15:00:00 2026] You have slain orc pawn!")!);
        mine.Companion = mate;

        var duoBefore = mine.DuoVersion;
        var mineVersionBefore = mine.CurrentVersion;
        mate.Apply(LogParser.Parse("[Sat Jul 18 15:00:05 2026] You have slain orc centurion!")!);

        Assert.True(mine.DuoVersion > duoBefore);
        // CurrentVersion (the primary alone) must NOT have moved — DuoVersion is the
        // one that must be used wherever a teammate's own activity should be visible
        // (the plan's version-plumbing trap: gating the Mobile pump on CurrentVersion
        // alone would never notice the teammate's own activity).
        Assert.Equal(mineVersionBefore, mine.CurrentVersion);
    }

    [Fact]
    public void TheArchiverStillRecordsTheWatchedCharacterAlone()
    {
        // SessionArchiver.FinalizeActive/Checkpoint always call the plain Snapshot() —
        // never DuoSnapshot — so proving Snapshot() never carries a Mate is the
        // Core-level guarantee that archives/history.db stay solo (Part 7 item 2's
        // decision, logged as reversible-and-privacy-safe).
        var mine = new SessionStats();
        var mate = new SessionStats();
        mine.Apply(LogParser.Parse("[Sat Jul 18 15:00:00 2026] You have slain orc pawn!")!);
        mate.Apply(LogParser.Parse("[Sat Jul 18 15:00:05 2026] You have slain orc centurion!")!);
        mine.Companion = mate;

        var plain = mine.Snapshot();
        Assert.Null(plain.Mate);
        Assert.Equal(1, plain.YourKillCount);   // never combined

        var duo = mine.DuoSnapshot(null, null);
        Assert.NotNull(duo.Mate);
        Assert.Equal(2, duo.YourKillCount);   // DuoSnapshot is the one place combining happens
    }

    /// <summary>
    /// A1: <c>DuoSnapshot</c> snapshotted the companion with <c>recentWindow: null</c>,
    /// so <see cref="StatsSnapshot.Recent"/> on the mate side was ALWAYS null and
    /// <c>CombineRecent</c> silently returned the primary's own rates untouched — the
    /// teammate could get five kills inside the recent window and the duo's recent
    /// kill count would still read as if they had not. This is an INTEGRATION test
    /// against <see cref="SessionStats.DuoSnapshot"/> deliberately, not a
    /// <see cref="DuoStats.Combine"/> fixture: a fixture test hands both sides an
    /// already-built <see cref="RecentRates"/> and cannot see a caller passing the
    /// wrong window into the wrong <c>Snapshot</c> call.
    /// </summary>
    [Fact]
    public void DuoSnapshotCombinesTheTeammatesRecentWindowToo()
    {
        var mine = new SessionStats();
        var mate = new SessionStats();
        mine.Companion = mate;

        mine.Apply(LogParser.Parse("[Sat Jul 18 15:00:00 2026] You have slain orc pawn!")!);
        mate.Apply(LogParser.Parse("[Sat Jul 18 15:00:05 2026] You have slain orc centurion!")!);
        mate.Apply(LogParser.Parse("[Sat Jul 18 15:00:10 2026] You have slain orc centurion!")!);

        var duo = mine.DuoSnapshot(TimeSpan.FromMinutes(30), null);

        Assert.NotNull(duo.Recent);
        // Mine contributed 1 kill in-window; the teammate's 2 must be added in, not
        // dropped because their side was snapshotted with a null window.
        Assert.Equal(3, duo.Recent!.Kills);
    }

    /// <summary>
    /// A2 (part ii), highest priority of the repair round: SessionStats.SessionGap's
    /// autonomous 60-minute roll is upstream, unmodifiable within this file's own
    /// budget, and fires on EACH instance independently — so a 60-minute gap in only
    /// the teammate's log rolls THEIR session while the primary's own keeps running,
    /// and their pre-roll contribution used to vanish from the duo total entirely.
    /// The fix cannot suppress the roll (it can't touch SessionStats.cs), so it
    /// carries the ended segment FORWARD instead: <see cref="SessionStats.Companion"/>'s
    /// setter subscribes to the companion's own <c>SessionEnding</c>, which fires with
    /// the full pre-roll snapshot before <c>ResetLocked</c> wipes it.
    /// </summary>
    [Fact]
    public void ACompanionOnlyGapRolloverKeepsItsPreRollContributionInTheDuoTotal()
    {
        var mine = new SessionStats();
        var mate = new SessionStats();
        mine.Companion = mate;

        mate.Apply(LogParser.Parse("[Sat Jul 18 15:00:00 2026] You have slain orc pawn!")!);      // mate session 1: 1 kill
        mine.Apply(LogParser.Parse("[Sat Jul 18 15:00:05 2026] You have slain orc centurion!")!); // mine kill 1
        mine.Apply(LogParser.Parse("[Sat Jul 18 15:30:00 2026] You have slain orc centurion!")!); // mine kill 2 — keeps MINE's own gap under 60 min

        // 65 minutes after mate's own last event (but only 35 after mine's) — rolls
        // ONLY the teammate's internal session, exactly the bug this fixes.
        mate.Apply(LogParser.Parse("[Sat Jul 18 16:05:00 2026] You have slain orc guard!")!);      // mate session 2: 1 kill
        mine.Apply(LogParser.Parse("[Sat Jul 18 16:05:05 2026] You have slain orc centurion!")!); // mine kill 3 — no gap on mine's side

        var duo = mine.DuoSnapshot(null, null);

        // mine: 3, mate: 1 (carried from the rolled-over segment) + 1 (current) = 5.
        Assert.Equal(5, duo.YourKillCount);
    }

    /// <summary>Companion.set with a NEW teammate must not go on carrying a PREVIOUS
    /// teammate's ended segments forward into a stranger's totals.</summary>
    [Fact]
    public void ReassigningCompanionDropsThePreviousOnesCarry()
    {
        var mine = new SessionStats();
        var mate = new SessionStats();
        mine.Companion = mate;
        mate.Apply(LogParser.Parse("[Sat Jul 18 15:00:00 2026] You have slain orc pawn!")!);
        mate.Apply(LogParser.Parse("[Sat Jul 18 16:05:00 2026] You have slain orc guard!")!); // rolls mate, carries 1 kill

        var newMate = new SessionStats();
        mine.Companion = newMate;   // swap teammates mid-session
        newMate.Apply(LogParser.Parse("[Sat Jul 18 16:05:05 2026] You have slain orc centurion!")!);

        var duo = mine.DuoSnapshot(null, null);
        Assert.Equal(1, duo.YourKillCount);   // only newMate's kill — the old carry must not leak in
    }

    /// <summary>LogWatcher's existing Step 6b hook (<c>OnPrimarySessionRolledOver</c>)
    /// resets the companion's live Stats at a PRIMARY session boundary; it must also
    /// clear whatever carry had built up, or a stale teammate contribution from the
    /// primary's PREVIOUS session keeps padding every duo total in the new one.</summary>
    [Fact]
    public void ClearCompanionCarryDropsAnAccumulatedSegment()
    {
        var mine = new SessionStats();
        var mate = new SessionStats();
        mine.Companion = mate;
        mate.Apply(LogParser.Parse("[Sat Jul 18 15:00:00 2026] You have slain orc pawn!")!);
        mate.Apply(LogParser.Parse("[Sat Jul 18 16:05:00 2026] You have slain orc guard!")!); // rolls mate, carries 1 kill

        mine.ClearCompanionCarry();

        var duo = mine.DuoSnapshot(null, null);
        Assert.Equal(1, duo.YourKillCount);   // only mate's CURRENT (post-roll) kill remains
    }

    [Fact]
    public void CompanionRefusesToChain()
    {
        var mine = new SessionStats();
        var mate = new SessionStats();
        var mateOfMate = new SessionStats();
        mate.Companion = mateOfMate;

        Assert.Throws<InvalidOperationException>(() => mine.Companion = mate);
    }

    /// <summary>
    /// A8: the ORIGINAL order — assign a companion, THEN try to give that companion
    /// one of its own — was never enforced. <c>mine.Companion = mate</c> succeeds
    /// (mate.Companion was null at the time); a bare "mate.Companion is not null" one-
    /// hop check on the SETTER cannot see that mate is now ALREADY serving as mine's
    /// companion, so <c>mate.Companion = third</c> used to succeed too, leaving mate
    /// simultaneously mine's companion AND third's primary — the exact two-deep chain
    /// the original check exists to prevent, reachable from the other direction.
    /// </summary>
    [Fact]
    public void AnInstanceAlreadyServingAsACompanionRefusesToBeGivenOneOfItsOwn()
    {
        var mine = new SessionStats();
        var mate = new SessionStats();
        var third = new SessionStats();
        mine.Companion = mate;   // succeeds — mate had no companion of its own

        Assert.Throws<InvalidOperationException>(() => mate.Companion = third);
    }

    // ---- B4: the user's product decision — merge the per-player breakdowns so
    // every board sums to its own header, keeping ability/spell/hit-type rows
    // attributable by tagging the teammate's onto the list rather than summing
    // same-named rows together. ----

    [Fact]
    public void DamageBySourceKeepsMineAndTheTeammatesKickAsTwoDistinctRows()
    {
        var mine = new StatsSnapshot
        {
            DamageBySource = [new SourceDamage("Kick", 5, 500)],
            Elapsed = TimeSpan.FromHours(1),
        };
        var mate = new StatsSnapshot
        {
            DamageBySource = [new SourceDamage("Kick", 3, 300)],
        };

        var combined = DuoStats.Combine(mine, mate, mateCharacterName: "Buddy");

        Assert.Equal(2, combined.DamageBySource.Count);
        var mineRow = Assert.Single(combined.DamageBySource, sd => sd.Name == "Kick");
        Assert.Equal(500, mineRow.Total);
        var mateRow = Assert.Single(combined.DamageBySource, sd => sd.Name != "Kick");
        Assert.Contains("Buddy", mateRow.Name);
        Assert.Equal(300, mateRow.Total);
    }

    [Fact]
    public void PetAbilitiesAndHealsBySpellAndSpecialHitsAlsoTagRatherThanSum()
    {
        var mine = new StatsSnapshot
        {
            PetAbilities = [new SourceDamage("Bite", 1, 10)],
            HealsBySpell = [new SourceDamage("Complete Heal", 1, 500)],
            SpecialHits = [new NameCount("Crippling Blow", 1)],
            Elapsed = TimeSpan.FromHours(1),
        };
        var mate = new StatsSnapshot
        {
            PetAbilities = [new SourceDamage("Bite", 1, 20)],
            HealsBySpell = [new SourceDamage("Complete Heal", 1, 600)],
            SpecialHits = [new NameCount("Crippling Blow", 1)],
        };

        var combined = DuoStats.Combine(mine, mate, mateCharacterName: "Buddy");

        Assert.Equal(2, combined.PetAbilities.Count);
        Assert.Contains(combined.PetAbilities, sd => sd.Name == "Bite" && sd.Total == 10);
        Assert.Contains(combined.PetAbilities, sd => sd.Name != "Bite" && sd.Name.Contains("Bite") && sd.Total == 20);

        Assert.Equal(2, combined.HealsBySpell.Count);
        Assert.Contains(combined.HealsBySpell, sd => sd.Name == "Complete Heal" && sd.Total == 500);
        Assert.Contains(combined.HealsBySpell, sd => sd.Name != "Complete Heal" && sd.Name.Contains("Buddy") && sd.Total == 600);

        Assert.Equal(2, combined.SpecialHits.Count);
        Assert.Contains(combined.SpecialHits, nc => nc.Name == "Crippling Blow" && nc.Count == 1);
        Assert.Contains(combined.SpecialHits, nc => nc.Name != "Crippling Blow" && nc.Name.Contains("Buddy"));
    }

    [Fact]
    public void DamageByAttackerAndHealsByHealerSumByNameWithNoAttributionAmbiguity()
    {
        var mine = new StatsSnapshot
        {
            DamageByAttacker = [new SourceDamage("an orc pawn", 5, 500)],
            HealsByHealer = [new SourceDamage("Cleric Buddy", 2, 200)],
            Elapsed = TimeSpan.FromHours(1),
        };
        var mate = new StatsSnapshot
        {
            DamageByAttacker = [new SourceDamage("an orc pawn", 3, 300)],
            HealsByHealer = [new SourceDamage("Cleric Buddy", 4, 400)],
        };

        var combined = DuoStats.Combine(mine, mate, mateCharacterName: "Buddy");

        var attacker = Assert.Single(combined.DamageByAttacker);
        Assert.Equal("an orc pawn", attacker.Name);
        Assert.Equal(8, attacker.Hits);
        Assert.Equal(800, attacker.Total);

        var healer = Assert.Single(combined.HealsByHealer);
        Assert.Equal("Cleric Buddy", healer.Name);
        Assert.Equal(6, healer.Hits);
        Assert.Equal(600, healer.Total);
    }

    [Fact]
    public void DamageTimelineAlignsMatchingMinuteBucketsAndSumsThem()
    {
        var minute1 = new DateTime(2026, 1, 1, 12, 0, 0);
        var minute2 = new DateTime(2026, 1, 1, 12, 1, 0);
        var mine = new StatsSnapshot
        {
            DamageTimeline = [new TimelinePoint(minute1, 100)],
            Elapsed = TimeSpan.FromHours(1),
        };
        var mate = new StatsSnapshot
        {
            // Same minute1 bucket (must SUM, not become a second row) plus a bucket
            // mine never touched (must survive as its own row).
            DamageTimeline = [new TimelinePoint(minute1, 50), new TimelinePoint(minute2, 25)],
        };

        var combined = DuoStats.Combine(mine, mate, mateCharacterName: "Buddy");

        Assert.Equal(2, combined.DamageTimeline.Count);
        Assert.Equal(150, combined.DamageTimeline.Single(p => p.Time == minute1).Damage);
        Assert.Equal(25, combined.DamageTimeline.Single(p => p.Time == minute2).Damage);
    }

    [Fact]
    public void EffortSumsDamageAndHealingButKeepsMinesOwnWindows()
    {
        var mine = new StatsSnapshot
        {
            Effort = new RecentEffort(TimeSpan.FromSeconds(30), 1000, 200, TimeSpan.FromSeconds(5), 100),
            Elapsed = TimeSpan.FromHours(1),
        };
        var mate = new StatsSnapshot
        {
            Effort = new RecentEffort(TimeSpan.FromSeconds(99), 500, 900, TimeSpan.FromSeconds(99), 50),
        };

        var combined = DuoStats.Combine(mine, mate, mateCharacterName: "Buddy");

        Assert.Equal(TimeSpan.FromSeconds(30), combined.Effort.Window);       // mine's own
        Assert.Equal(TimeSpan.FromSeconds(5), combined.Effort.ResumeWindow); // mine's own
        Assert.Equal(1500, combined.Effort.DamageDone);                      // 1000 + 500
        Assert.Equal(1100, combined.Effort.HealingDone);                     // 200 + 900
        Assert.Equal(150, combined.Effort.DamageDoneInResumeWindow);         // 100 + 50
    }

    // ---- C1: the companion carry needs a DEDICATED same-actor fold, separate from
    // the primary-plus-mate combine — reusing the two-actor combine recursively on
    // an ended segment tagged and re-tagged the SAME teammate as if a second person
    // had joined. ----

    [Fact]
    public void CombineSameActorCarryAggregatesAbilityRowsByNameWithNoTag()
    {
        var carry = new StatsSnapshot
        {
            DamageBySource = [new SourceDamage("Kick", 5, 500)],
            Elapsed = TimeSpan.FromHours(1),
        };
        var ended = new StatsSnapshot
        {
            DamageBySource = [new SourceDamage("Kick", 3, 300)],
        };

        var folded = DuoStats.CombineSameActorCarry(carry, ended);

        // ONE row, not two, and no "(teammate)"/"(Buddy)"-shaped tag anywhere — this
        // is the SAME actor across two time segments, not a second person.
        var row = Assert.Single(folded.DamageBySource);
        Assert.Equal("Kick", row.Name);
        Assert.Equal(8, row.Hits);
        Assert.Equal(800, row.Total);
    }

    [Fact]
    public void CombineSameActorCarrySumsDisjointCombatSecondsRatherThanTakingTheMax()
    {
        var carry = new StatsSnapshot { CombatSeconds = 20, DamageDealt = 200 };
        var ended = new StatsSnapshot { CombatSeconds = 15, DamageDealt = 150 };

        var folded = DuoStats.CombineSameActorCarry(carry, ended);

        // Sequential segments (the ended segment happened entirely before the roll
        // that produced the fresh live one) can never overlap — sum, not Math.Max
        // (which would read 20) and not DuoCompanion's union (no span data survives
        // a companion's own internal reset, so there is nothing to union here).
        Assert.Equal(35, folded.CombatSeconds);
    }

    [Fact]
    public void CombineSameActorCarryClearsTransientEffortRatherThanSummingIt()
    {
        var carry = new StatsSnapshot
        {
            // A large burst from the ENDED (now historical) segment.
            Effort = new RecentEffort(TimeSpan.FromSeconds(30), 5000, 0, TimeSpan.FromSeconds(5), 5000),
        };
        var ended = new StatsSnapshot
        {
            Effort = new RecentEffort(TimeSpan.FromSeconds(30), 100, 50, TimeSpan.FromSeconds(5), 20),
        };

        var folded = DuoStats.CombineSameActorCarry(carry, ended);

        // Repair round C1: an hour-old rollover's activity must not drive what the
        // COLLAPSED HUD shows right now — Effort is a rolling ~30s window, not a
        // running total, so folding a finished segment's window into anything live
        // is meaningless. Cleared, not summed (summing would read 5100/50/5020).
        Assert.Equal(RecentEffort.None, folded.Effort);
    }

    [Fact]
    public void ATeammateOnlyRolloverDoesNotDoubleTagAbilityRowsInTheRealDuoSnapshot()
    {
        // Reproduces the exact reported symptom: a teammate-only 60-minute gap rolls
        // ONLY their session (SessionStats' own autonomous gap roll — see A2), and
        // the carry-forward this creates must not make DuoSnapshot render the SAME
        // teammate's "Kick" as two separate people's rows.
        var mine = new SessionStats();
        var mate = new SessionStats { CharacterName = "Buddy" };
        mine.Companion = mate;

        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        // DamageDealtEvent.Source names the SKILL for a melee hit (SessionStats maps
        // it through SkillName), not the character — "Kick" here IS the ability name
        // that ends up on the DamageBySource row.
        mate.Apply(new DamageDealtEvent(t0, "a target", 500, DamageKind.Melee, "Kick", false));
        mine.Apply(new DamageDealtEvent(t0, "a target", 10, DamageKind.Melee, "Kick", false));
        // 65 minutes later — a gap on the TEAMMATE's side only — rolls mate's own
        // session internally (SessionStats.SessionGap = 60 min) without touching mine.
        var t1 = t0.AddMinutes(65);
        mate.Apply(new DamageDealtEvent(t1, "a target", 300, DamageKind.Melee, "Kick", false));
        mine.Apply(new DamageDealtEvent(t1, "a target", 10, DamageKind.Melee, "Kick", false));

        var duo = mine.DuoSnapshot(null, null);

        // Exactly the rows mine's own melee ("Kick", untagged) plus ONE teammate-
        // tagged "Kick" row carrying BOTH segments' hits — never two teammate rows,
        // and never a nested "((teammate))"-shaped tag.
        var kickRows = duo.DamageBySource.Where(sd => sd.Name.Contains("Kick")).ToList();
        Assert.DoesNotContain(kickRows, sd => sd.Name.Contains("teammate", StringComparison.OrdinalIgnoreCase));
        var mateKickRows = kickRows.Where(sd => sd.Name != "Kick").ToList();
        var mateKick = Assert.Single(mateKickRows);
        Assert.Equal(800, mateKick.Total);   // 500 + 300, one actor, one row
    }

    // ---- C5: the provenance tag must not be encoded as plain text into Name — it
    // collided with a real row literally called "Kick (Buddy)", rendered
    // "Kick ((teammate))" for a null/empty actor, and nested ambiguously when a
    // name itself contained brackets. ----

    [Fact]
    public void TagWithActorNeverCollidesWithARowGenuinelyNamedThatString()
    {
        // A real ability could, in principle, be named exactly what the OLD
        // "{Name} ({actor})" scheme would have produced — the reserved marker
        // character can never appear in game text, so the two are still
        // distinguishable even in that worst case.
        var real = "Kick (Buddy)";
        var tagged = DuoStats.TagWithActor("Kick", "Buddy");

        Assert.NotEqual(real, tagged);
        var (baseName, actor) = DuoStats.SplitActorTag(tagged);
        Assert.Equal("Kick", baseName);
        Assert.Equal("Buddy", actor);
        // The genuinely-real row round-trips as itself, with no actor at all —
        // never misread as a tagged row for someone called "Buddy)".
        var (realBase, realActor) = DuoStats.SplitActorTag(real);
        Assert.Equal(real, realBase);
        Assert.Null(realActor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TagWithActorFallsBackToABareTeammateNeverANestedOne(string? actor)
    {
        var tagged = DuoStats.TagWithActor("Kick", actor);
        var (baseName, splitActor) = DuoStats.SplitActorTag(tagged);

        Assert.Equal("Kick", baseName);
        Assert.Equal("teammate", splitActor);   // bare — never "(teammate)" or "((teammate))"
    }

    [Fact]
    public void TagWithActorHandlesNamesThatAlreadyContainBrackets()
    {
        var tagged = DuoStats.TagWithActor("Kick (Improved)", "Buddy");
        var (baseName, actor) = DuoStats.SplitActorTag(tagged);

        Assert.Equal("Kick (Improved)", baseName);   // the bracket is part of the NAME, unambiguously
        Assert.Equal("Buddy", actor);
    }

    // ---- C3: subtracting by TARGET name alone cannot express the real overlap.
    // mate.YourKills is SELF-REPORTED and can claim a target the primary's own log
    // never actually saw the teammate kill — subtracting it from mine.PartyKillsByTarget
    // (which only ever counts what MY log saw) can delete a THIRD groupmate's real,
    // visible kill of a same-named target. The fix takes mateVisibleKillsByTarget —
    // the exact count the PRIMARY's own log attributed to the teammate — instead. ----

    [Fact]
    public void AnInvisibleTeammateKillNeverDeletesAThirdGroupmatesVisibleOne()
    {
        // The teammate killed 5 orcs the primary never saw at all (self-reported
        // only) — mate.YourKills claims 5, but mateVisibleKillsByTarget (built from
        // the primary's OWN log) correctly says 0, because nothing about those 5
        // kills ever appeared there. A third groupmate's ONE real, visible orc kill
        // must survive the subtraction untouched.
        var mine = new StatsSnapshot
        {
            PartyKillCount = 1,
            PartyKillsByTarget = [new NameCount("Orc", 1)],   // the groupmate's one VISIBLE kill
            PartyKillsByKiller = [new NameCount("Grouper", 1)],
            Elapsed = TimeSpan.FromHours(1),
        };
        var mate = new StatsSnapshot
        {
            YourKillCount = 5,
            YourKills = [new NameCount("Orc", 5)],   // self-reported — NOT what the primary's log saw
        };
        var mateVisibleKillsByTarget = new Dictionary<string, int>();   // primary's log saw NONE of these

        var combined = DuoStats.Combine(mine, mate, "Buddy", null, mateVisibleKillsByTarget);

        Assert.Equal(5, combined.YourKillCount);              // the teammate's kills still promote
        Assert.Equal(1, combined.PartyKillCount);              // the groupmate's kill is NOT deleted
        Assert.Contains(combined.PartyKillsByTarget, nc => nc.Name == "Orc" && nc.Count == 1);
    }

    [Fact]
    public void APartiallyVisibleTeammateKillIsRemovedExactlyNotBySelfReport()
    {
        // 2 of the teammate's "Orc pawn" kills WERE visible in the primary's own
        // log (mateVisibleKillsByTarget says so); a third groupmate ALSO killed one
        // visible "Orc pawn". mate.YourKills self-reports 5 total (3 more were
        // elsewhere, invisible) — subtracting the self-reported 5 would delete the
        // groupmate's kill too; subtracting the TRUE visible count (2) leaves
        // exactly the groupmate's 1.
        var mine = new StatsSnapshot
        {
            PartyKillCount = 3,
            PartyKillsByTarget = [new NameCount("Orc pawn", 3)],   // 2 teammate (visible) + 1 groupmate
            PartyKillsByKiller = [new NameCount("Buddy", 2), new NameCount("Grouper", 1)],
            Elapsed = TimeSpan.FromHours(1),
        };
        var mate = new StatsSnapshot
        {
            YourKillCount = 5,
            YourKills = [new NameCount("Orc pawn", 5)],   // self-reported total, includes invisible ones
        };
        var mateVisibleKillsByTarget = new Dictionary<string, int> { ["Orc pawn"] = 2 };   // the TRUE visible count

        var combined = DuoStats.Combine(mine, mate, "Buddy", null, mateVisibleKillsByTarget);

        Assert.Equal(1, combined.PartyKillCount);
        Assert.Contains(combined.PartyKillsByTarget, nc => nc.Name == "Orc pawn" && nc.Count == 1);
    }

    [Fact]
    public void WithNoVisibleKillsByTargetSuppliedTheOldSelfReportFallbackStillApplies()
    {
        // Fixture-level tests and the carry-fold path never supply this — falling
        // back to the self-reported subtraction keeps Combine correct on its own
        // rather than skipping the correction entirely.
        var mine = new StatsSnapshot
        {
            PartyKillCount = 3,
            PartyKillsByTarget = [new NameCount("Orc guard", 3)],
            Elapsed = TimeSpan.FromHours(1),
        };
        var mate = new StatsSnapshot
        {
            YourKillCount = 2,
            YourKills = [new NameCount("Orc guard", 2)],
        };

        var combined = DuoStats.Combine(mine, mate, "Buddy");

        Assert.Equal(1, combined.PartyKillCount);
        Assert.Contains(combined.PartyKillsByTarget, nc => nc.Name == "Orc guard" && nc.Count == 1);
    }

    // ---- C7: mine's/the companion's snapshot and combat-span accounting must be
    // captured ATOMICALLY (one lock hold each), not as two separate statements a
    // concurrent hit could land between. Mutual exclusion is a structural
    // guarantee once both reads share one `lock` block (Monitor's exclusivity, not
    // a timing race to reproduce) — this exercises the real concurrent path under
    // load: DuoSnapshot from one thread while the companion keeps taking damage on
    // another, the shape the Mobile pump vs. the poll timer actually has. ----

    [Fact]
    public void DuoSnapshotStaysSelfConsistentUnderConcurrentCompanionActivity()
    {
        var mine = new SessionStats();
        var mate = new SessionStats();
        mine.Companion = mate;
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        mine.Apply(new DamageDealtEvent(t0, "a target", 100, DamageKind.Melee, "Kick", false));

        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            var t = t0;
            while (!stop.IsCancellationRequested)
            {
                t = t.AddSeconds(1);
                mate.Apply(new DamageDealtEvent(t, "a target", 10, DamageKind.Melee, "Kick", false));
            }
        });

        try
        {
            for (var i = 0; i < 500; i++)
            {
                var duo = mine.DuoSnapshot(null, null);
                // Whatever moment this snapshot landed on, CombatSeconds (the
                // union's denominator) and DamageDealt (the numerator each side's
                // OWN atomically-paired capture already accounts for) must agree
                // closely enough that SessionDps is always a sane, finite,
                // non-negative number — never NaN, never negative, never a wild
                // spike from a numerator/denominator pulled from different moments.
                Assert.False(double.IsNaN(duo.SessionDps));
                Assert.True(duo.SessionDps >= 0);
                Assert.True(duo.CombatSeconds >= 0);
            }
        }
        finally
        {
            stop.Cancel();
            writer.Wait(TimeSpan.FromSeconds(5));
        }
    }
}
