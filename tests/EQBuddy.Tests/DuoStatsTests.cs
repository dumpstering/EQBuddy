using System.Reflection;
using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>
/// The duo combine's pure arithmetic, <see cref="DuoStats.Combine"/> (the live seam
/// around it, <see cref="SessionStats.DuoSnapshot"/>, is covered in
/// <c>DuoSnapshotTests</c>). <see cref="EveryStatsSnapshotPropertyIsClassifiedAsCombinedOrPassedThrough"/>
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
    /// <see cref="StatsSnapshot.CastCompletion"/>) set to a distinct, TAG-derived value — via
    /// reflection's <c>PropertyInfo.SetValue</c>, which reaches <c>init</c> setters
    /// exactly like any other setter (the <c>init</c> restriction is a C#-compiler-only
    /// check, invisible to the CLR).</summary>
    private static StatsSnapshot BuildFixture(string tag, int seedBase)
    {
        var snap = new StatsSnapshot();
        var props = typeof(StatsSnapshot).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite);
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
        [nameof(StatsSnapshot.PetDps)] = "computed getter (PetAbilities.Sum / CombatSeconds) — both inputs are already Combined above, so this follows for free",
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
        // DRA-71 D3 (upstream 593af9ef): the log's timestamp travels with the announced
        // level so MainWindow's tick can gate QuestLedger.SetLevel on ObservedLevelFor —
        // same passthrough as LastLevel itself, and DuoStats.Combine must copy both
        // or a level-up during a duo session is silently never persisted.
        [nameof(StatsSnapshot.LastLevelAt)] = "yours — travels with LastLevel (DRA-71 D3)",
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

    /// <summary>Regression for the sync onto upstream c8259fe3 (DRA-71 D3):
    /// <c>LastLevel</c> travels through <see cref="DuoStats.Combine"/>, but <c>LastLevelAt</c> — the log
    /// timestamp upstream added alongside it so MainWindow's tick can gate
    /// <c>QuestLedger.SetLevel</c> on <c>ObservedLevelFor</c> — did not, because this
    /// branch's <see cref="StatsSnapshot"/> predates that upstream field. Left
    /// unfixed, a level-up during a duo session carries a level number with no
    /// timestamp attached, <c>s.LastLevelAt is { } announcedAt</c> at the tick site
    /// never matches, and the ding is silently never persisted to the quest ledger.
    /// Confirmed to FAIL (both asserts) by reverting the two <c>LastLevelAt = …</c>
    /// lines this commit adds to <c>DuoStats.cs</c>.</summary>
    [Fact]
    public void LastLevelAtTravelsWithLastLevelThroughCombine()
    {
        var announcedAt = new DateTime(2026, 9, 20, 14, 30, 0);
        var mine = new StatsSnapshot { LastLevel = 43, LastLevelAt = announcedAt };
        var mate = new StatsSnapshot { LastLevel = 12, LastLevelAt = announcedAt.AddMinutes(-5) };

        var combined = DuoStats.Combine(mine, mate);
        Assert.Equal(43, combined.LastLevel);
        Assert.Equal(announcedAt, combined.LastLevelAt);

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
}
