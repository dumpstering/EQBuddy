namespace EQBuddy.Core;

/// <summary>
/// The field-by-field rule for combining the watched character's own
/// <see cref="StatsSnapshot"/> with a teammate's — the ONE place two sessions become one
/// number. Pure and static on purpose: no lock, no I/O, nothing session-scoped, so it is
/// cheap to fold once per teammate (<see cref="TeammateCombine"/>) and simple to unit
/// test with hand-built fixtures.
///
/// <b>The non-duplication argument, in one place:</b> a teammate's kill reaches YOUR
/// log as a third-party <c>KillEvent</c> (a party kill, not <c>YourKills</c>) and
/// reaches THEIR derived session as their own <c>YourKills</c> — so summing
/// <see cref="StatsSnapshot.YourKillCount"/> is safe, but the party-kill rows must lose
/// exactly those kills or the same kill shows twice. The identical shape holds for
/// damage (their swings reach your stats only as third-party events that never add to
/// <c>DamageDealt</c>) and heals (your heal on them is outgoing on your side and incoming
/// on theirs — different buckets). XP is the one number where summing is simply
/// meaningless: <see cref="XpEvent.Percent"/> is a percentage of THAT character's own
/// level bar, so <see cref="StatsSnapshot.XpPercent"/> and everything derived from it
/// (XpPerHour, HoursToLevel, AA) stays the watched character's own. A teammate derived
/// from your log never has loot, coin or XP at all — your log never prints theirs.
///
/// <b>Deliberately deferred:</b> <see cref="StatsSnapshot.Mobs"/> ships as <c>mine</c>
/// unmerged. Merging per-creature rows correctly needs a mob-identity join while leaving
/// per-character fields alone. The gap: a mob a teammate kills alone never appears in the
/// "Mob farming" rollup. Documented, not silently dropped.
///
/// <b>Time comes from the caller.</b> A <see cref="StatsSnapshot"/> carries no span
/// history, so combat seconds, the recent-window rates and the live "current" DPS are
/// computed by <see cref="TeammateCombine"/> as UNIONS over the live instances' spans
/// and handed in; without them this falls back to <c>Math.Max</c> / a plain sum, which
/// is right only for fixtures.
/// </summary>
public static class DuoStats
{
    /// <summary>Mirrors the private <c>SessionStats.MaxRecentLoot</c> (250) — kept here
    /// too because this class must stay outside the SessionStats*.cs ratchet glob;
    /// duplicating the literal costs nothing, exposing the private constant would cost a
    /// SessionStats.cs line.</summary>
    private const int MaxRecentLoot = 250;

    /// <summary>Combines <paramref name="mine"/> with <paramref name="mate"/>'s own
    /// snapshot. <paramref name="mate"/> null returns <paramref name="mine"/> BY
    /// REFERENCE — the no-teammate path costs nothing.
    ///
    /// <paramref name="mateCharacterName"/> tags the teammate's ability rows.
    /// <paramref name="combatSecondsOverride"/> is the union of every side's combat spans
    /// (null falls back to <c>Math.Max</c>). <paramref name="mateVisibleKillsByTarget"/>
    /// and <paramref name="mateVisibleKillsByKiller"/> are the EXACT party-kill rows the
    /// teammate's own kills and deaths bumped in <paramref name="mine"/>'s log — subtracted
    /// row by row, so a kill made before the teammate joined stays a party kill and the
    /// killer rows keep summing to <see cref="StatsSnapshot.PartyKillCount"/>. Null
    /// target counts fall back to subtracting the teammate's self-reported
    /// <see cref="StatsSnapshot.YourKills"/>; null killer counts fall back to dropping the
    /// rows named for the teammate or their pet (fixture-level callers only).
    /// <paramref name="recentWindowTotals"/> and <paramref name="currentDpsOverride"/>
    /// recompute the recent and live rates from summed amounts over one union (null sums
    /// the two rates — fixtures only). <paramref name="versionOverride"/> replaces the
    /// summed <see cref="StatsSnapshot.Version"/> so the combined snapshot carries exactly
    /// <see cref="SessionStats.DuoVersion"/>.</summary>
    public static StatsSnapshot Combine(StatsSnapshot mine, StatsSnapshot? mate,
        string? mateCharacterName = null, double? combatSecondsOverride = null,
        IReadOnlyDictionary<string, int>? mateVisibleKillsByTarget = null,
        RecentWindowTotals? recentWindowTotals = null,
        IReadOnlyDictionary<string, int>? mateVisibleKillsByKiller = null,
        double? currentDpsOverride = null,
        long? versionOverride = null)
    {
        if (mate is null) return mine;

        var combinedKills = mine.YourKillCount + mate.YourKillCount;

        // mine.PartyKillsByTarget/ByKiller already hold the teammate's kills (and their
        // deaths — upstream files "Garg has been slain by X!" as a party kill too) as
        // third-party lines from mine's own log. Those exact rows come out here; the
        // header is then resummed from the residual target rows, so it always agrees
        // with its own breakdown.
        var residualPartyKillsByTarget = mine.PartyKillsByTarget;
        var residualPartyKillsByKiller = mine.PartyKillsByKiller;
        if (mateVisibleKillsByTarget is not null)
            residualPartyKillsByTarget = Subtract(mine.PartyKillsByTarget, mateVisibleKillsByTarget);
        else if (mateCharacterName is { Length: > 0 } && mate.YourKills.Count > 0)
            residualPartyKillsByTarget = Subtract(mine.PartyKillsByTarget,
                mate.YourKills.ToDictionary(nc => nc.Name, nc => nc.Count, StringComparer.OrdinalIgnoreCase));
        if (mateVisibleKillsByKiller is not null)
            residualPartyKillsByKiller = Subtract(mine.PartyKillsByKiller, mateVisibleKillsByKiller);
        else if (mateCharacterName is { Length: > 0 })
            residualPartyKillsByKiller = [.. mine.PartyKillsByKiller.Where(nc =>
                !nc.Name.Equals(mateCharacterName, StringComparison.OrdinalIgnoreCase)
                && (mate.PetName.Length == 0 || !nc.Name.Equals(mate.PetName, StringComparison.OrdinalIgnoreCase)))];
        var residualPartyKillCount = residualPartyKillsByTarget.Sum(nc => nc.Count);
        var combinedDamage = mine.DamageDealt + mate.DamageDealt;
        var combinedHealing = mine.HealingDone + mate.HealingDone;
        var combinedCopper = mine.Copper + mate.Copper;
        // The union of every side's combat spans (two sequential fights add, two
        // overlapping ones do not double); Math.Max only for fixture-level callers.
        var combinedCombatSeconds = combatSecondsOverride ?? Math.Max(mine.CombatSeconds, mate.CombatSeconds);
        // Repair round B4 (the user's product decision, superseding A6's hold on
        // this hand-off): every per-player breakdown board now sums to its own
        // header. Ability/spell/hit-type rows have no source-name field that says
        // WHO performed them, so mine's own rows are left untouched and mate's are
        // tagged with their character name (or "(teammate)" if it isn't known)
        // rather than summed into a
        // same-named row, which would silently erase the fact that two different
        // people used the same ability. Person-keyed rows (who hit/healed YOU) carry
        // no such ambiguity — the name is already the external party — and sum by
        // name normally, same shape as MergeSold above.
        var combinedDamageBySource = MergeAbilityRows(mine.DamageBySource, mate.DamageBySource, mateCharacterName);
        var combinedPetAbilities = MergeAbilityRows(mine.PetAbilities, mate.PetAbilities, mateCharacterName);
        var combinedHealsBySpell = MergeAbilityRows(mine.HealsBySpell, mate.HealsBySpell, mateCharacterName);
        var combinedSpecialHits = MergeNameCountRows(mine.SpecialHits, mate.SpecialHits, mateCharacterName);
        var combinedDamageByAttacker = MergeSourceDamageByName(mine.DamageByAttacker, mate.DamageByAttacker);
        var combinedHealsByHealer = MergeSourceDamageByName(mine.HealsByHealer, mate.HealsByHealer);
        var combinedDamageTimeline = MergeTimeline(mine.DamageTimeline, mate.DamageTimeline);
        var combinedEffort = new RecentEffort(
            mine.Effort.Window,
            mine.Effort.DamageDone + mate.Effort.DamageDone,
            mine.Effort.HealingDone + mate.Effort.HealingDone,
            mine.Effort.ResumeWindow,
            mine.Effort.DamageDoneInResumeWindow + mate.Effort.DamageDoneInResumeWindow);
        // Computed once, ahead of the initializer below, so MergeLoot (repair round
        // A6) can read real pickup chronology off it instead of trusting whichever
        // side's loop happened to run second.
        var combinedRecentLoot = mine.RecentLoot.Concat(mate.RecentLoot)
            .OrderByDescending(l => l.Time).Take(MaxRecentLoot).ToList();
        // Zero guards: an idle duo (no combat yet) must not divide by zero seconds/hours.
        var hours = Math.Max(mine.Elapsed.TotalHours, 1.0 / 60);
        var activeHours = Math.Max(mine.ActiveSeconds / 3600.0, 1.0 / 60);

        return new StatsSnapshot
        {
            // ---- COMBINE ----
            Version = versionOverride ?? mine.Version + mate.Version,
            YourKillCount = combinedKills,
            YourKills = MergeCounts(mine.YourKills, mate.YourKills),
            KillsPerHour = combinedKills / hours,
            KillsPerActiveHour = combinedKills / activeHours,
            DamageDealt = combinedDamage,
            MeleeDamage = mine.MeleeDamage + mate.MeleeDamage,
            SpellDamage = mine.SpellDamage + mate.SpellDamage,
            DotDamage = mine.DotDamage + mate.DotDamage,
            DirectSpellDamage = mine.DirectSpellDamage + mate.DirectSpellDamage,
            HitCount = mine.HitCount + mate.HitCount,
            CritCount = mine.CritCount + mate.CritCount,
            MissCount = mine.MissCount + mate.MissCount,
            MaxHit = Math.Max(mine.MaxHit, mate.MaxHit),
            MaxHitDesc = mate.MaxHit > mine.MaxHit ? mate.MaxHitDesc : mine.MaxHitDesc,
            CombatSeconds = combinedCombatSeconds,
            SessionDps = combinedCombatSeconds > 0 ? combinedDamage / combinedCombatSeconds : 0,
            // Recomputed by the caller from summed live damage over the union of the
            // live spans; summing two rates with different denominators is the
            // fixture-only fallback.
            CurrentDps = currentDpsOverride ?? mine.CurrentDps + mate.CurrentDps,
            DamageTaken = mine.DamageTaken + mate.DamageTaken,
            AvoidedIncoming = mine.AvoidedIncoming + mate.AvoidedIncoming,
            MeleeHitsTaken = mine.MeleeHitsTaken + mate.MeleeHitsTaken,
            HealingDone = combinedHealing,
            HealingReceived = mine.HealingReceived + mate.HealingReceived,
            Hps = combinedCombatSeconds > 0 ? combinedHealing / combinedCombatSeconds : 0,
            LootTotal = mine.LootTotal + mate.LootTotal,
            Loot = MergeLoot(mine.Loot, mate.Loot, combinedRecentLoot),
            RecentLoot = combinedRecentLoot,
            Copper = combinedCopper,
            CorpseCopper = mine.CorpseCopper + mate.CorpseCopper,
            VendorCopper = mine.VendorCopper + mate.VendorCopper,
            SalesCount = mine.SalesCount + mate.SalesCount,
            SoldItems = MergeSold(mine.SoldItems, mate.SoldItems),
            CopperPerHour = (long)(combinedCopper / hours),
            CopperPerActiveHour = (long)(combinedCopper / activeHours),
            Recent = recentWindowTotals is { } rwt
                ? CombineRecentExact(mine.Recent, mate.Recent, rwt)
                : CombineRecent(mine.Recent, mate.Recent),
            // Repair round B4: see the comment on mateLabel above for why these tag
            // rather than sum, and DECISIONS.md for the product call.
            DamageBySource = combinedDamageBySource,
            PetAbilities = combinedPetAbilities,
            HealsBySpell = combinedHealsBySpell,
            SpecialHits = combinedSpecialHits,
            DamageByAttacker = combinedDamageByAttacker,
            HealsByHealer = combinedHealsByHealer,
            DamageTimeline = combinedDamageTimeline,
            Effort = combinedEffort,

            // ---- STAYS MINE ----
            LastLocation = mine.LastLocation,
            LocationTrail = mine.LocationTrail,
            SessionStart = mine.SessionStart,
            LastEventTime = mine.LastEventTime,
            Elapsed = mine.Elapsed,
            // Correctness requirement, not a preference: PartyKillCount already counts
            // every party kill visible in the WATCHED character's own log — a
            // teammate's kill is already in there as a third-party line. Summing would
            // double it. Repair round A3: the teammate's OWN share of that count is
            // ALSO removed here (see residualPartyKillCount above), because it just
            // got promoted into combinedKills a few lines up — leaving both in would
            // count it twice a different way.
            PartyKillCount = residualPartyKillCount,
            PartyKillsByTarget = residualPartyKillsByTarget,
            PartyKillsByKiller = residualPartyKillsByKiller,
            Deaths = mine.Deaths,
            // Repair round A4, decision documented in DuoStatsTests's
            // CoinDropsAndBiggestDropStayPrimaryOnly: CoinDrops is an EVENT COUNT and
            // BiggestDrop the largest single RECEIPT, so summing/maxing them across a
            // corpse copper split (each player gets their own "you receive" line)
            // double-counts the event and reports a personal share as the duo's take.
            // Correlating receipts per corpse needs data this redesign doesn't track;
            // both stay primary-only rather than a plausible-looking wrong number.
            // Copper (the raw total) has no such ambiguity and stays summed above.
            CoinDrops = mine.CoinDrops,
            BiggestDrop = mine.BiggestDrop,
            PetName = mine.PetName,
            CharmedSince = mine.CharmedSince,
            CurrentTargets = mine.CurrentTargets,
            RegenTicks = mine.RegenTicks,
            RegenEstimatedHealed = mine.RegenEstimatedHealed,
            RegenSpell = mine.RegenSpell,
            RuneGainCount = mine.RuneGainCount,
            RuneGainPoints = mine.RuneGainPoints,
            RuneBlockCount = mine.RuneBlockCount,
            RuneBlockStreak = mine.RuneBlockStreak,
            RuneBlockStreakMax = mine.RuneBlockStreakMax,
            Crafted = mine.Crafted,
            CraftedTotal = mine.CraftedTotal,
            Fashioned = mine.Fashioned,
            FashionedTotal = mine.FashionedTotal,
            Upgraded = mine.Upgraded,
            // XP is a percentage of THAT character's own level bar — the single most
            // important "no" in the combine table. Summing 40% and 60% is not 100%.
            XpPercent = mine.XpPercent,
            XpTicks = mine.XpTicks,
            XpPerHour = mine.XpPerHour,
            HoursToLevel = mine.HoursToLevel,
            AaGained = mine.AaGained,
            AaAbilities = mine.AaAbilities,
            AaTotal = mine.AaTotal,
            AaPerHour = mine.AaPerHour,
            Levels = mine.Levels,
            LastLevel = mine.LastLevel,
            LastLevelAt = mine.LastLevelAt,
            SkillUps = mine.SkillUps,
            SkillUpTotal = mine.SkillUpTotal,
            // Each character has their own faction standing with an NPC faction.
            Faction = mine.Faction,
            Zones = mine.Zones,
            CurrentZone = mine.CurrentZone,
            Fizzles = mine.Fizzles,
            Resists = mine.Resists,
            Blocked = mine.Blocked,
            CastsStarted = mine.CastsStarted,
            CastsInterrupted = mine.CastsInterrupted,
            ActiveSeconds = mine.ActiveSeconds,
            XpPerActiveHour = mine.XpPerActiveHour,
            Tracked = mine.Tracked,
            Markers = mine.Markers,
            LastFight = mine.LastFight,
            RecentEncounters = mine.RecentEncounters,
            Encounters = mine.Encounters,
            EncounterCount = mine.EncounterCount,
            // Deferred (6a) — see class doc. Ships as mine, with the gap documented
            // rather than silently accepted.
            Mobs = mine.Mobs,
            CurrentStance = mine.CurrentStance,
            Stances = mine.Stances,
            CurrentInvocation = mine.CurrentInvocation,
            Invocations = mine.Invocations,
            AreaSpells = mine.AreaSpells,
            Procs = mine.Procs,
            SpellResists = mine.SpellResists,
            InferredClass = mine.InferredClass,
            InferredClasses = mine.InferredClasses,
        };
    }

    /// <summary>Each row minus its count in <paramref name="subtract"/>; a row that
    /// reaches zero is dropped, and a count larger than the row floors at zero.</summary>
    private static List<NameCount> Subtract(List<NameCount> rows, IReadOnlyDictionary<string, int> subtract) =>
        [.. rows.Select(nc => nc with { Count = nc.Count - subtract.GetValueOrDefault(nc.Name) })
            .Where(nc => nc.Count > 0)];

    private static List<NameCount> MergeCounts(List<NameCount> a, List<NameCount> b)
    {
        var merged = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var nc in a) merged[nc.Name] = merged.GetValueOrDefault(nc.Name) + nc.Count;
        foreach (var nc in b) merged[nc.Name] = merged.GetValueOrDefault(nc.Name) + nc.Count;
        return [.. merged.Select(kv => new NameCount(kv.Key, kv.Value)).OrderByDescending(nc => nc.Count)];
    }

    /// <summary>Repair round C5: the provenance tag lives after a RESERVED marker
    /// character, not encoded as free-text parentheses into <c>Name</c> — the old
    /// <c>"{Name} ({actor})"</c> scheme collided with any REAL row genuinely called
    /// that, rendered <c>"Kick ((teammate))"</c> for a null/empty actor, and nested
    /// ambiguously when the ability's own name already contained brackets (e.g.
    /// "Kick (Improved)"). <see cref="ActorTagMarker"/> is a Unicode Private Use Area
    /// codepoint (U+E000) no eqlog line can ever contain, so splitting on it is
    /// unambiguous in both directions regardless of what either half looks like.
    /// <see cref="StatsSnapshot.DamageBySource"/> and its siblings are fixed as
    /// <c>List&lt;SourceDamage&gt;</c>/<c>List&lt;NameCount&gt;</c> in the budget-locked
    /// SessionStats.cs, so a dedicated structured "who performed this" field on the
    /// row itself is not available here — this is the escaped-format fallback the
    /// finding names for exactly that case.</summary>
    private const char ActorTagMarker = (char)0xE000;

    internal static string TagWithActor(string name, string? actor) =>
        $"{name}{ActorTagMarker}{(actor is { Length: > 0 } ? actor : "teammate")}";

    /// <summary>The inverse of <see cref="TagWithActor"/>. A name with no marker at
    /// all (mine's own row) returns itself with a null actor.</summary>
    public static (string BaseName, string? Actor) SplitActorTag(string taggedName)
    {
        var idx = taggedName.IndexOf(ActorTagMarker);
        return idx < 0 ? (taggedName, null) : (taggedName[..idx], taggedName[(idx + 1)..]);
    }

    /// <summary>Human-readable label; the encoded key remains separate for attribution.</summary>
    public static string DisplayActorTag(string name)
    {
        var (baseName, actor) = SplitActorTag(name);
        return actor is null ? baseName : $"{baseName} ({actor})";
    }

    /// <summary>Repair round B4: mine's ability/spell rows pass through UNCHANGED —
    /// their names are the ability, not the person — and the teammate's own rows get
    /// <see cref="TagWithActor"/> applied, so "Kick" and "Kick␀Buddy" (rendered by a
    /// consumer, not this file) stand as two distinct, correctly-attributed rows
    /// rather than one that no longer says two different people kicked.</summary>
    private static List<SourceDamage> MergeAbilityRows(List<SourceDamage> mine, List<SourceDamage> mate, string? mateCharacterName) =>
        [.. mine.Concat(mate.Select(sd => sd with { Name = TagWithActor(sd.Name, mateCharacterName) }))
            .OrderByDescending(sd => sd.Total)];

    /// <summary>Same shape as <see cref="MergeAbilityRows"/> for the <see cref="NameCount"/>
    /// hit-type breakdown (<see cref="StatsSnapshot.SpecialHits"/>).</summary>
    private static List<NameCount> MergeNameCountRows(List<NameCount> mine, List<NameCount> mate, string? mateCharacterName) =>
        [.. mine.Concat(mate.Select(nc => nc with { Name = TagWithActor(nc.Name, mateCharacterName) }))
            .OrderByDescending(nc => nc.Count)];

    /// <summary>Repair round B4: <see cref="StatsSnapshot.DamageByAttacker"/> and
    /// <see cref="StatsSnapshot.HealsByHealer"/> are keyed by the EXTERNAL party's
    /// name — an attacker hitting you, or a healer healing you — so there is no
    /// "who performed this" ambiguity to lose the way there is for an ability row:
    /// the same mob hitting both of you, or the same healer topping off both of
    /// you, is genuinely additive to the duo's totals. Summed by name like
    /// <c>MergeSold</c> below.</summary>
    private static List<SourceDamage> MergeSourceDamageByName(List<SourceDamage> a, List<SourceDamage> b)
    {
        var merged = new Dictionary<string, SourceDamage>(StringComparer.OrdinalIgnoreCase);
        void Add(SourceDamage sd)
        {
            if (merged.TryGetValue(sd.Name, out var cur))
                merged[sd.Name] = cur with
                {
                    Hits = cur.Hits + sd.Hits,
                    Total = cur.Total + sd.Total,
                    Crits = cur.Crits + sd.Crits,
                    ActiveSeconds = cur.ActiveSeconds + sd.ActiveSeconds,
                    MinHit = cur.MinHit == 0 ? sd.MinHit : sd.MinHit == 0 ? cur.MinHit : Math.Min(cur.MinHit, sd.MinHit),
                    MaxHit = Math.Max(cur.MaxHit, sd.MaxHit),
                    Misses = cur.Misses + sd.Misses,
                };
            else merged[sd.Name] = sd;
        }
        foreach (var sd in a) Add(sd);
        foreach (var sd in b) Add(sd);
        return [.. merged.Values.OrderByDescending(sd => sd.Total)];
    }

    /// <summary>Repair round B4: <see cref="StatsSnapshot.DamageTimeline"/> buckets on
    /// an ABSOLUTE per-minute <see cref="DateTime"/> (<c>SessionStats.AddTimelineDamage</c>'s
    /// <c>t.Ticks / TimeSpan.TicksPerMinute</c>), so mine's and the teammate's buckets
    /// already share the same real-world minute boundaries — aligning them is exactly
    /// matching on <see cref="TimelinePoint.Time"/> and summing, never concatenating
    /// (which would leave two rows for one minute and double whatever chart reads it).</summary>
    private static List<TimelinePoint> MergeTimeline(List<TimelinePoint> a, List<TimelinePoint> b)
    {
        var merged = new Dictionary<DateTime, long>();
        foreach (var p in a) merged[p.Time] = merged.GetValueOrDefault(p.Time) + p.Damage;
        foreach (var p in b) merged[p.Time] = merged.GetValueOrDefault(p.Time) + p.Damage;
        return [.. merged.Select(kv => new TimelinePoint(kv.Key, kv.Value)).OrderBy(p => p.Time)];
    }

    /// <summary><paramref name="recentLoot"/> (repair round A6) is the combined,
    /// newest-first pickup list — real chronology, unlike the aggregated
    /// <see cref="LootDetail"/> rows this merges, which carry a source but no
    /// timestamp. The old "last writer wins" left <c>b</c> (mate) as LastSource for
    /// every item BOTH players looted, since mate is always folded in second,
    /// regardless of which pickup actually happened later. An item outside the
    /// recent-loot cap (250, very rare for LastSource specifically — it would take
    /// 250 MORE recent pickups of anything else after it) falls back to the old
    /// last-writer rule rather than losing a source entirely.</summary>
    private static List<LootDetail> MergeLoot(List<LootDetail> a, List<LootDetail> b, List<LootPickup> recentLoot)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lastSource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(LootDetail d)
        {
            counts[d.Item] = counts.GetValueOrDefault(d.Item) + d.Count;
            lastSource.TryAdd(d.Item, d.LastSource);   // fallback seed, overridden below when chronology is known
        }
        foreach (var d in a) Add(d);
        foreach (var d in b) Add(d);
        var chronologyKnown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pickup in recentLoot)   // already newest-first — first occurrence per item IS the latest
        {
            if (!counts.ContainsKey(pickup.Item) || !chronologyKnown.Add(pickup.Item)) continue;
            lastSource[pickup.Item] = pickup.Source;
        }
        return [.. counts.Select(kv => new LootDetail(kv.Key, kv.Value, lastSource[kv.Key]))
            .OrderByDescending(d => d.Count)];
    }

    private static List<SoldDetail> MergeSold(List<SoldDetail> a, List<SoldDetail> b)
    {
        var merged = new Dictionary<string, (int Count, long Copper)>(StringComparer.OrdinalIgnoreCase);
        void Add(SoldDetail d)
        {
            (int Count, long Copper) cur = merged.TryGetValue(d.Item, out var v) ? v : (0, 0L);
            merged[d.Item] = (cur.Count + d.Count, cur.Copper + d.Copper);
        }
        foreach (var d in a) Add(d);
        foreach (var d in b) Add(d);
        return [.. merged.Select(kv => new SoldDetail(kv.Key, kv.Value.Count, kv.Value.Copper))
            .OrderByDescending(d => d.Copper)];
    }

    /// <summary>Window/HasFullWindow/XpPercent/XpPerHour anchor on <paramref name="mine"/>
    /// (percentages of a level bar, exactly like the top-level XP fields); Kills/Copper
    /// sum. <b>Audit finding (kept as the no-window-data fallback only):</b> Dps/Hps here
    /// are each side's OWN already-divided rate summed together, which double-reports
    /// two non-overlapping fights as one continuous one — see
    /// <see cref="CombineRecentExact"/>, used whenever a caller supplies
    /// <see cref="RecentWindowTotals"/>. Either side missing (a snapshot taken before the
    /// first Recent window closed) returns the other unchanged rather than fabricating
    /// one.</summary>
    private static RecentRates? CombineRecent(RecentRates? mine, RecentRates? mate)
    {
        if (mine is null) return mate;
        if (mate is null) return mine;
        return mine with
        {
            Kills = mine.Kills + mate.Kills,
            Copper = mine.Copper + mate.Copper,
            Dps = mine.Dps + mate.Dps,
            Hps = mine.Hps + mate.Hps,
        };
    }

    /// <summary>The raw numerator each side actually dealt in the shared recent window,
    /// plus the exact UNION of every side's combat spans clipped to that window (built
    /// by <see cref="TeammateCombine"/>'s running fold, the same shape as the
    /// whole-session <c>combatSecondsOverride</c>). Passing this to <see cref="Combine"/> makes it recompute Dps/Hps ONCE from
    /// the summed numerator over the union denominator, instead of summing two
    /// independently-windowed rates (<see cref="CombineRecent"/>'s bug).</summary>
    public readonly record struct RecentWindowTotals(
        double MineDamage, double MineHealing, double MateDamage, double MateHealing, double CombatSecondsInWindow);

    /// <summary>Audit finding fix: Dps/Hps are RECOMPUTED from the summed window
    /// numerator over the exact union denominator — never summed as two already-divided
    /// rates. Kills/Copper still sum (true, unrelated to the rate arithmetic). Either
    /// side missing returns the other unchanged, matching <see cref="CombineRecent"/>.</summary>
    private static RecentRates? CombineRecentExact(RecentRates? mine, RecentRates? mate, RecentWindowTotals w)
    {
        if (mine is null) return mate;
        if (mate is null) return mine;
        var dmg = w.MineDamage + w.MateDamage;
        var healed = w.MineHealing + w.MateHealing;
        return mine with
        {
            Kills = mine.Kills + mate.Kills,
            Copper = mine.Copper + mate.Copper,
            Dps = w.CombatSecondsInWindow > 0 ? dmg / w.CombatSecondsInWindow : 0,
            Hps = w.CombatSecondsInWindow > 0 ? healed / w.CombatSecondsInWindow : 0,
        };
    }
}
