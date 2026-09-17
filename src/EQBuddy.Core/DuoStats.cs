namespace EQBuddy.Core;

/// <summary>
/// The field-by-field rule for combining the watched character's own
/// <see cref="StatsSnapshot"/> with their teammate's — the ONE place the duo redesign
/// (plan Part 2) actually merges two sessions. Pure and static on purpose: no lock, no
/// I/O, nothing session-scoped, so it costs nothing to call from a memo (see
/// <c>DuoCompanion.cs</c>) and nothing to unit test with hand-built fixtures.
///
/// <b>The non-duplication argument, in one place:</b> a teammate's kill reaches YOUR
/// log as a third-party <c>KillEvent</c> (not <c>YourKills</c>) and their log as their
/// own <c>YourKills</c> — so summing <see cref="StatsSnapshot.YourKillCount"/> is safe,
/// but <see cref="StatsSnapshot.PartyKillCount"/> (which already counts party kills
/// visible in YOUR OWN log, teammate's included) would DOUBLE if summed, so it stays
/// yours alone. The identical shape holds for damage (their swings reach your log only
/// as third-party events that never add to <c>DamageDealt</c>), heals (their heal on
/// you is outgoing in THEIR log, incoming in yours — different buckets) and loot/money
/// (every loot/coin regex in <c>LogParser.cs</c> is first-person; there is no
/// third-party loot line to double-admit). XP is the one number where summing is
/// simply meaningless: <see cref="XpEvent.Percent"/> is a percentage of THAT
/// character's own level bar, so <see cref="StatsSnapshot.XpPercent"/> and everything
/// derived from it (XpPerHour, HoursToLevel, AA) stays the watched character's own.
///
/// <b>Deliberately deferred (6a, per the coordinator's scope cut):</b> <see cref="StatsSnapshot.Mobs"/>
/// ships as <c>mine</c> unmerged. Merging per-creature kill/loot rows correctly needs a
/// mob-identity join (kills, loot, coin range, level range) while leaving percentage
/// and per-character fields (<c>Xp</c>, <c>Factions</c>, <c>Considers</c>, <c>Zone</c>)
/// alone — real work, scoped out of this pass. The gap: a mob a teammate solos never
/// appears in the "Mob farming" rollup at all. Documented, not silently dropped.
///
/// <b>CombatSeconds (repair round B3, corrected from A6's initial "reported, not
/// fixed" call):</b> <see cref="StatsSnapshot.CombatSeconds"/> is the real union of
/// both sides' timestamped combat spans, computed by
/// <see cref="DuoCompanion.UnionCombatSeconds"/> against the LIVE
/// <see cref="SessionStats"/> instances and handed to <see cref="Combine"/> as
/// <c>combatSecondsOverride</c> — this class never needed a StatsSnapshot property to
/// reach that data, because <c>DuoCompanion.cs</c> is part of the SAME partial class
/// as the private fields it reads (<c>_combatSpans</c>, <c>_closedCombatSeconds</c>,
/// the still-open span), at zero cost to SessionStats.cs's own sync budget.
/// </summary>
public static class DuoStats
{
    /// <summary>Mirrors the private <c>SessionStats.MaxRecentLoot</c> (250) — kept here
    /// too because this class must stay outside the SessionStats*.cs ratchet glob (plan
    /// Part 2a); duplicating the literal costs nothing, exposing the private constant
    /// would cost a SessionStats.cs line this file cannot spend.</summary>
    private const int MaxRecentLoot = 250;

    /// <summary>Combines <paramref name="mine"/> with <paramref name="mate"/>'s own
    /// snapshot. <paramref name="mate"/> null (no teammate selected) returns
    /// <paramref name="mine"/> BY REFERENCE — the no-teammate path must cost nothing,
    /// and reference identity is what lets a memo above this call skip rebuilding
    /// entirely when nothing changed. <paramref name="mateCharacterName"/> (repair
    /// round A3) is the teammate's OWN character name — optional and defaulted to
    /// null for the carry-forward folds in <c>DuoCompanion.cs</c>, which combine
    /// two purely companion-side snapshots and have no separate "residual party"
    /// concept to correct; the real primary+mate combine in <c>DuoSnapshot</c> always
    /// supplies it. <paramref name="combatSecondsOverride"/> (repair round B3) is the
    /// real union of both LIVE instances' timestamped combat spans, computed by
    /// <c>DuoCompanion.UnionCombatSeconds</c> — this pure static method has no access
    /// to the live <see cref="SessionStats"/> objects the span data lives on, only to
    /// their already-built snapshots, so the union is computed by the caller and
    /// handed in. Null (the carry-forward folds, and any direct fixture-level test)
    /// falls back to <c>Math.Max</c>. <paramref name="mateVisibleKillsByTarget"/>
    /// (repair round C3) is the TRUE per-target count of the teammate's promoted
    /// kills AS SEEN IN MINE'S OWN LOG — see
    /// <c>DuoCompanion._promotedPartyKillsByTarget</c>'s own doc for why this
    /// replaces subtracting <paramref name="mate"/>'s SELF-REPORTED
    /// <see cref="StatsSnapshot.YourKills"/>, which can claim a target mine's own
    /// log never actually saw the teammate kill and delete a genuine THIRD
    /// groupmate's visible kill of the same name. Null (no primary
    /// <see cref="SessionStats"/> to have tracked it — the carry-fold path, or a
    /// direct fixture-level test) falls back to the old self-reported
    /// subtraction.</summary>
    public static StatsSnapshot Combine(StatsSnapshot mine, StatsSnapshot? mate,
        string? mateCharacterName = null, double? combatSecondsOverride = null,
        IReadOnlyDictionary<string, int>? mateVisibleKillsByTarget = null)
    {
        if (mate is null) return mine;

        var combinedKills = mine.YourKillCount + mate.YourKillCount;

        // Repair round A3: mine.PartyKillsByTarget/ByKiller already contain the
        // teammate's (and their pet's) kills as THIRD-PARTY lines from mine's own
        // log — the instant those are promoted into combinedKills above, leaving the
        // party breakdown untouched double-shows them (one kill renders "1 (+1)").
        // The killer breakdown is keyed by NAME, so the teammate's character name
        // and pet name are removed outright: nobody else shares either exact
        // string — that half was never ambiguous.
        //
        // Repair round C3: the TARGET breakdown is the one that WAS ambiguous.
        // mine.PartyKillsByTarget aggregates ACROSS every third-party killer who
        // hit that target, so a raw subtraction needs to know exactly how much of
        // a target's count is the teammate's — mateVisibleKillsByTarget is that
        // exact number (built from the SAME third-party KillEvents that populated
        // mine.PartyKillsByTarget in the first place, so it can never exceed what
        // that row actually contains). The self-reported mate.YourKills fallback
        // stays for callers that never tracked the true visible count.
        var residualPartyKillsByTarget = mine.PartyKillsByTarget;
        var residualPartyKillsByKiller = mine.PartyKillsByKiller;
        if (mateVisibleKillsByTarget is not null)
        {
            residualPartyKillsByTarget = [.. mine.PartyKillsByTarget
                .Select(nc => nc with { Count = nc.Count - mateVisibleKillsByTarget.GetValueOrDefault(nc.Name) })
                .Where(nc => nc.Count > 0)];
        }
        else if (mateCharacterName is { Length: > 0 } && mate.YourKills.Count > 0)
        {
            var mateKillsByTarget = mate.YourKills.ToDictionary(nc => nc.Name, nc => nc.Count, StringComparer.OrdinalIgnoreCase);
            residualPartyKillsByTarget = [.. mine.PartyKillsByTarget
                .Select(nc => nc with { Count = nc.Count - mateKillsByTarget.GetValueOrDefault(nc.Name) })
                .Where(nc => nc.Count > 0)];
        }
        if (mateCharacterName is { Length: > 0 })
        {
            residualPartyKillsByKiller = [.. mine.PartyKillsByKiller.Where(nc =>
                !nc.Name.Equals(mateCharacterName, StringComparison.OrdinalIgnoreCase)
                && (mate.PetName.Length == 0 || !nc.Name.Equals(mate.PetName, StringComparison.OrdinalIgnoreCase)))];
        }
        // Recomputed from the residual list rather than subtracted separately, so the
        // header always agrees with its own breakdown by construction.
        var residualPartyKillCount = residualPartyKillsByTarget.Sum(nc => nc.Count);
        var combinedDamage = mine.DamageDealt + mate.DamageDealt;
        var combinedHealing = mine.HealingDone + mate.HealingDone;
        var combinedCopper = mine.Copper + mate.Copper;
        // Repair round B3: CombatSeconds is the real UNION of both sides' timestamped
        // combat spans — Math.Max undercounts whenever the two fights don't fully
        // overlap (two independent 10s fights would read as 10s, not 20s), and a
        // plain sum double-counts whenever they DO overlap. combatSecondsOverride is
        // that union, computed by DuoCompanion.UnionCombatSeconds against the live
        // SessionStats instances (this static method only ever sees snapshots, which
        // carry no span data) — see that method's own doc for how it handles the
        // 2048-entry trim on very long sessions. Falling back to Math.Max when no
        // override is supplied keeps this method correct on its own for the carry-
        // forward folds (which combine two snapshots, not two live instances) and for
        // direct fixture-level tests.
        var combinedCombatSeconds = combatSecondsOverride ?? Math.Max(mine.CombatSeconds, mate.CombatSeconds);
        // Repair round B4 (the user's product decision, superseding A6's hold on
        // this hand-off): every per-player breakdown board now sums to its own
        // header. Ability/spell/hit-type rows have no source-name field that says
        // WHO performed them, so mine's own rows are left untouched and mate's are
        // tagged with their character name (or "(teammate)" if it isn't known —
        // mirrors MezTracker.Teammate.cs's own fallback) rather than summed into a
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
            Version = mine.Version + mate.Version,
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
            // AMBIGUOUS, resolved: summing two independently-windowed live rates is an
            // approximation, accepted knowingly (plan Part 2c) rather than picking a
            // single side's number and hiding the other's activity entirely.
            CurrentDps = mine.CurrentDps + mate.CurrentDps,
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
            Recent = CombineRecent(mine.Recent, mate.Recent),
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
            // The escape hatch (plan Part 2c): every side-by-side teammate number
            // (their XP%, their level, their deaths) reads through here instead of
            // getting its own dedicated field. [JsonIgnore]'d on StatsSnapshot itself —
            // a teammate's session is never archived or wired to Mobile.
            Mate = mate,

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

    /// <summary>
    /// Repair round C1: a DEDICATED fold for the SAME actor across two time
    /// segments — the companion's own pre-rollover carry and its just-ended (or
    /// just-live) segment — kept entirely separate from <see cref="Combine"/>,
    /// which is for two DIFFERENT people. The bug this replaces called
    /// <c>Combine</c> recursively on the carry: every pass tags whatever came in as
    /// "mate" with an actor label, so an ended segment already tagged once by an
    /// earlier fold got tagged AGAIN by the next one — one teammate's own "Kick"
    /// rows came out as <c>Kick␀Buddy</c> from the live segment and
    /// <c>Kick␀teammate␀Buddy</c> from the folded-twice historical one, as if two
    /// different people had kicked.
    ///
    /// Three things a same-actor fold must do differently from a two-actor combine:
    ///  - Ability/spell/hit-type rows AGGREGATE BY NAME (the same actor really did
    ///    do all of it), never tag — <see cref="MergeSourceDamageByName"/> and
    ///    <see cref="MergeCounts"/> are the exact "sum by matching key" primitives
    ///    <see cref="Combine"/> already uses for the person-keyed rows, reused here
    ///    because that operation IS what a same-actor fold needs everywhere.
    ///  - <see cref="StatsSnapshot.CombatSeconds"/> is SUMMED, not unioned or
    ///    maxed: <paramref name="carry"/> and <paramref name="ended"/> are
    ///    sequential by construction — the ended segment finished entirely before
    ///    the internal gap-roll that started the fresh one — so they cannot
    ///    overlap, and no live span data survives a companion's own reset for a
    ///    real union to read anyway.
    ///  - <see cref="StatsSnapshot.Effort"/> is CLEARED (<see cref="RecentEffort.None"/>),
    ///    not summed: it is a ~30-SECOND ROLLING WINDOW anchored on a log
    ///    timestamp, not a running total, so adding an ENDED segment's window to a
    ///    live one let an hour-old rollover's burst decide whether today's
    ///    collapsed HUD shows damage or healing. When live is true, retain only
    ///    the newer live segment's CurrentDps, Recent and Effort instead.
    ///
    /// <paramref name="ended"/> — the chronologically NEWER segment — supplies
    /// every "current state" field (XP%, zone, stance, faction, and so on): those
    /// describe where the actor IS right now, and <paramref name="carry"/>'s copy
    /// of them is, by definition, stale the moment a rollover has happened.
    /// <paramref name="carry"/> null (nothing has rolled over yet) returns
    /// <paramref name="ended"/> unchanged — the common case costs nothing.
    /// </summary>
    internal static StatsSnapshot CombineSameActorCarry(StatsSnapshot? carry, StatsSnapshot ended, bool live = false)
    {
        if (carry is null) return ended;

        var combinedCombatSeconds = carry.CombatSeconds + ended.CombatSeconds;
        var combinedDamage = carry.DamageDealt + ended.DamageDealt;
        var combinedHealing = carry.HealingDone + ended.HealingDone;
        var combinedCopper = carry.Copper + ended.Copper;
        var combinedRecentLoot = carry.RecentLoot.Concat(ended.RecentLoot)
            .OrderByDescending(l => l.Time).Take(MaxRecentLoot).ToList();

        return new StatsSnapshot
        {
            // ---- ADDITIVE: the same actor's running totals across both segments ----
            Version = ended.Version,
            YourKillCount = carry.YourKillCount + ended.YourKillCount,
            YourKills = MergeCounts(carry.YourKills, ended.YourKills),
            DamageDealt = combinedDamage,
            MeleeDamage = carry.MeleeDamage + ended.MeleeDamage,
            SpellDamage = carry.SpellDamage + ended.SpellDamage,
            DotDamage = carry.DotDamage + ended.DotDamage,
            DirectSpellDamage = carry.DirectSpellDamage + ended.DirectSpellDamage,
            HitCount = carry.HitCount + ended.HitCount,
            CritCount = carry.CritCount + ended.CritCount,
            MissCount = carry.MissCount + ended.MissCount,
            MaxHit = Math.Max(carry.MaxHit, ended.MaxHit),
            MaxHitDesc = ended.MaxHit > carry.MaxHit ? ended.MaxHitDesc : carry.MaxHitDesc,
            // Repair round C1: SUM, not union/max — see this method's own doc.
            CombatSeconds = combinedCombatSeconds,
            SessionDps = combinedCombatSeconds > 0 ? combinedDamage / combinedCombatSeconds : 0,
            // Historical folds have no current activity; a live fold keeps only the newer segment's.
            CurrentDps = live ? ended.CurrentDps : 0,
            DamageTaken = carry.DamageTaken + ended.DamageTaken,
            AvoidedIncoming = carry.AvoidedIncoming + ended.AvoidedIncoming,
            MeleeHitsTaken = carry.MeleeHitsTaken + ended.MeleeHitsTaken,
            HealingDone = combinedHealing,
            HealingReceived = carry.HealingReceived + ended.HealingReceived,
            Hps = combinedCombatSeconds > 0 ? combinedHealing / combinedCombatSeconds : 0,
            LootTotal = carry.LootTotal + ended.LootTotal,
            Loot = MergeLoot(carry.Loot, ended.Loot, combinedRecentLoot),
            RecentLoot = combinedRecentLoot,
            Copper = combinedCopper,
            CorpseCopper = carry.CorpseCopper + ended.CorpseCopper,
            VendorCopper = carry.VendorCopper + ended.VendorCopper,
            SalesCount = carry.SalesCount + ended.SalesCount,
            SoldItems = MergeSold(carry.SoldItems, ended.SoldItems),
            CoinDrops = carry.CoinDrops + ended.CoinDrops,
            BiggestDrop = Math.Max(carry.BiggestDrop, ended.BiggestDrop),
            RegenTicks = carry.RegenTicks + ended.RegenTicks,
            RegenEstimatedHealed = carry.RegenEstimatedHealed + ended.RegenEstimatedHealed,
            RuneGainCount = carry.RuneGainCount + ended.RuneGainCount,
            RuneGainPoints = carry.RuneGainPoints + ended.RuneGainPoints,
            RuneBlockCount = carry.RuneBlockCount + ended.RuneBlockCount,
            RuneBlockStreakMax = Math.Max(carry.RuneBlockStreakMax, ended.RuneBlockStreakMax),
            Crafted = MergeCounts(carry.Crafted, ended.Crafted),
            CraftedTotal = carry.CraftedTotal + ended.CraftedTotal,
            Fashioned = MergeCounts(carry.Fashioned, ended.Fashioned),
            FashionedTotal = carry.FashionedTotal + ended.FashionedTotal,
            Upgraded = MergeCounts(carry.Upgraded, ended.Upgraded),
            XpTicks = carry.XpTicks + ended.XpTicks,
            AaGained = carry.AaGained + ended.AaGained,
            SkillUpTotal = carry.SkillUpTotal + ended.SkillUpTotal,
            Fizzles = carry.Fizzles + ended.Fizzles,
            Resists = carry.Resists + ended.Resists,
            Blocked = carry.Blocked + ended.Blocked,
            CastsStarted = carry.CastsStarted + ended.CastsStarted,
            CastsInterrupted = carry.CastsInterrupted + ended.CastsInterrupted,
            ActiveSeconds = carry.ActiveSeconds + ended.ActiveSeconds,
            AaTotal = Math.Max(carry.AaTotal, ended.AaTotal),
            // Repair round C1: aggregated BY NAME — the same actor, never tagged.
            DamageBySource = MergeSourceDamageByName(carry.DamageBySource, ended.DamageBySource),
            PetAbilities = MergeSourceDamageByName(carry.PetAbilities, ended.PetAbilities),
            HealsBySpell = MergeSourceDamageByName(carry.HealsBySpell, ended.HealsBySpell),
            SpecialHits = MergeCounts(carry.SpecialHits, ended.SpecialHits),
            DamageByAttacker = MergeSourceDamageByName(carry.DamageByAttacker, ended.DamageByAttacker),
            HealsByHealer = MergeSourceDamageByName(carry.HealsByHealer, ended.HealsByHealer),
            DamageTimeline = MergeTimeline(carry.DamageTimeline, ended.DamageTimeline),

            // ---- NOT MEANINGFUL for a folded carry — recomputed or cleared ----
            KillsPerHour = 0,
            KillsPerActiveHour = 0,
            CopperPerHour = 0,
            CopperPerActiveHour = 0,
            XpPerHour = 0,
            XpPerActiveHour = 0,
            HoursToLevel = null,
            Recent = live ? ended.Recent : null,
            // Repair round C1: cleared, not summed — see this method's own doc.
            Effort = live ? ended.Effort : RecentEffort.None,

            // ---- CURRENT STATE: the newer segment's own, never the stale carry's ----
            LastLocation = ended.LastLocation,
            LocationTrail = ended.LocationTrail,
            SessionStart = ended.SessionStart,
            LastEventTime = ended.LastEventTime,
            Elapsed = ended.Elapsed,
            PetName = ended.PetName,
            CharmedSince = ended.CharmedSince,
            CurrentTargets = ended.CurrentTargets,
            XpPercent = ended.XpPercent,
            AaAbilities = ended.AaAbilities,
            Levels = ended.Levels,
            LastLevel = ended.LastLevel,
            SkillUps = ended.SkillUps,
            Faction = ended.Faction,
            Zones = ended.Zones,
            CurrentZone = ended.CurrentZone,
            CurrentStance = ended.CurrentStance,
            Stances = ended.Stances,
            CurrentInvocation = ended.CurrentInvocation,
            Invocations = ended.Invocations,
            AreaSpells = ended.AreaSpells,
            Procs = ended.Procs,
            SpellResists = ended.SpellResists,
            InferredClass = ended.InferredClass,
            InferredClasses = ended.InferredClasses,
            Deaths = ended.Deaths,
            Markers = ended.Markers,
            LastFight = ended.LastFight,
            RecentEncounters = ended.RecentEncounters,
            Encounters = ended.Encounters,
            EncounterCount = ended.EncounterCount,
            Mobs = ended.Mobs,
            Tracked = ended.Tracked,

            // ---- Unused downstream (the real combine never reads a mate's copy of
            // these — see DuoSnapshot/Combine), kept simple rather than re-derived ----
            PartyKillCount = ended.PartyKillCount,
            PartyKillsByTarget = ended.PartyKillsByTarget,
            PartyKillsByKiller = ended.PartyKillsByKiller,
        };
    }

    private static List<NameCount> MergeCounts(List<NameCount> a, List<NameCount> b)
    {
        var merged = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var nc in a) merged[nc.Name] = merged.GetValueOrDefault(nc.Name) + nc.Count;
        foreach (var nc in b) merged[nc.Name] = merged.GetValueOrDefault(nc.Name) + nc.Count;
        return [.. merged.Select(kv => new NameCount(kv.Key, kv.Value)).OrderByDescending(nc => nc.Count)];
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
    /// all (mine's own row, or one from a same-actor carry fold that has not yet
    /// reached the final combine — see <see cref="CombineSameActorCarry"/>) returns
    /// itself with a null actor.</summary>
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
    /// (percentages of a level bar, exactly like the top-level XP fields); Kills/Copper/
    /// Dps/Hps sum. Either side missing (a snapshot taken before the first Recent window
    /// closed) returns the other unchanged rather than fabricating one.</summary>
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
}
