namespace EQBuddy.Core;

/// <summary>
/// Holds one isolated <see cref="SessionStats"/> per teammate, fed entirely from the
/// PRIMARY player's own log — there is no second file, no second machine, no synced
/// copy of anything. Every teammate event reaches this class as a line REWRITTEN from
/// a primary log line by <see cref="TeammatePerspective.Rewrite"/> into that teammate's
/// first person, then parsed by the UNCHANGED <see cref="LogParser"/> exactly as if the
/// teammate's own client had printed it.
///
/// <b>The isolation invariant is identical to the file-based feature's own (see the
/// superseded <c>TeammateLogTail</c>'s class doc): a teammate's <see cref="SessionStats"/>
/// here is a trivial <c>new SessionStats()</c> — no durable store attached, no event
/// subscribed, never handed to the archiver/checkpoint/Mobile wire. Nothing here can
/// leak a teammate's data into the primary's ledgers, because nothing here is ever
/// given the keys.</b>
///
/// <b>Only what the log actually shows reaches a teammate's stats.</b> Per the design
/// survey, that is melee/spell/DoT/damage-shield damage and misses (both directions),
/// kills, deaths, heals (both directions) and casts — never XP, AA, loot, coin or
/// faction, which the log never states for anyone but the primary and are therefore
/// simply never produced by <see cref="TeammatePerspective"/> in the first place, not
/// filtered out here after the fact.
/// </summary>
public sealed class DerivedTeammates
{
    private readonly TeammateRoster _roster = new();
    private readonly Dictionary<string, SessionStats> _stats = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every teammate this session has ever applied an event for — a superset
    /// of the CURRENT roster, since a name dropped from the roster (an exclusion
    /// learned later, or removed from the manual list) still owns whatever isolated
    /// stats it already accrued rather than losing them retroactively.</summary>
    public IReadOnlyCollection<string> KnownTeammates => _stats.Keys;

    /// <summary>Names auto-detected from group join/invite/tell lines so far — see
    /// <see cref="TeammateRoster.AutoDetected"/>.</summary>
    public IReadOnlyCollection<string> AutoDetected => _roster.AutoDetected;

    /// <summary>Feed one primary-log line. <paramref name="primaryName"/> and
    /// <paramref name="primaryPetName"/> are read fresh on every call (a pet can be
    /// resummoned mid-session under a new name) rather than captured once. Cheap when
    /// the roster is empty — the auto-detector still runs (it is what LETS the roster
    /// stop being empty), but <see cref="TeammatePerspective.Rewrite"/> is never called
    /// against zero candidate names.
    ///
    /// <b>Every already-known teammate's own session clock is kept moving in lockstep
    /// with the primary's</b>, whether or not this particular line names them: upstream's
    /// <see cref="SessionStats.Apply"/> rolls an instance over on ITS OWN 60-minute gap
    /// since its last applied event, and a derived teammate's instance only ever
    /// receives an event on a line naming them — so a teammate silent for over an hour
    /// (while the PRIMARY keeps playing, never gapping) used to roll independently and
    /// lose everything accrued so far, and one silent for over an hour BETWEEN two
    /// primary sessions used to carry stale totals into the fresh one (nothing ever
    /// told their instance the primary had moved on). A harmless <see cref="RawLineEvent"/>
    /// tick — the same event type upstream's own <c>ObserveRawLine</c> applies for an
    /// unmatched text-watch line, ignored by every stat except the session clock itself
    /// (SessionStats.cs: <c>e is not RawLineEvent and not RaidChatterEvent</c> excludes
    /// it from active-play time, and its <c>switch</c> has no case for it) — is applied
    /// to every OTHER known teammate for the same timestamp this line carries. A
    /// teammate who DID receive a real rewritten event from this line is skipped (no
    /// point ticking twice for one line), and the primary's own multi-hour gaps still
    /// roll every teammate over in step, because the tick's timestamp is the same one
    /// that would trigger the primary's own rollover.</summary>
    public void Observe(DateTime ts, string msg, string? primaryName, string? primaryPetName,
        IReadOnlyCollection<string>? manualNames)
    {
        _roster.Observe(msg);
        var roster = _roster.Roster(primaryName, primaryPetName, manualNames);

        var appliedTo = roster.Count == 0 ? null : ApplyRewrittenLines(ts, msg, primaryName, roster);

        foreach (var (name, stats) in _stats)
            if (appliedTo is null || !appliedTo.Contains(name))
                stats.Apply(new RawLineEvent(ts, msg));
    }

    private HashSet<string> ApplyRewrittenLines(DateTime ts, string msg, string? primaryName,
        IReadOnlyCollection<string> roster)
    {
        var appliedTo = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in TeammatePerspective.Rewrite(msg, primaryName ?? "", roster))
        {
            var evt = LogParser.Parse(ts, line.Line);
            if (evt is null) continue;
            // Fold a teammate's owned pet in as one of THEIR rows, tagged "(pet)" on
            // whatever field names the ability, rather than a same-named row that
            // would silently merge the pet's swing into the owner's own (design
            // survey §3: "fold into the owner's numbers with a (pet) skill row").
            // Kills/deaths/misses carry no such field and pass through unchanged —
            // a pet's kill still counts as the owner's kill, matching how upstream
            // already folds the PRIMARY's own pet kills into YourKillCount.
            if (line.IsPet) evt = TagPet(evt);
            GetOrCreate(line.Actor).Apply(evt);
            appliedTo.Add(line.Actor);
        }
        return appliedTo;
    }

    private static GameEvent TagPet(GameEvent evt) => evt switch
    {
        DamageDealtEvent d => d with { Source = TagName(d.Source) },
        HealEvent h => h with { Spell = TagName(h.Spell) },
        MissEvent m when m.Ability.Length > 0 => m with { Ability = TagName(m.Ability) },
        _ => evt,
    };

    private static string TagName(string name) => name.Length == 0 ? "(pet)" : $"{name} (pet)";

    private SessionStats GetOrCreate(string actor)
    {
        if (_stats.TryGetValue(actor, out var s)) return s;
        s = new SessionStats { CharacterName = actor };
        _stats[actor] = s;
        return s;
    }

    /// <summary>The roster this instant — see <see cref="TeammateRoster.Roster"/>.
    /// Exposed for Options → Behavior (detected names shown, add/remove) and for the
    /// combine step, which only ever folds in stats for CURRENT roster members — a
    /// name dropped from the roster stops contributing to the combined view even
    /// though <see cref="KnownTeammates"/> still remembers it existed.</summary>
    public IReadOnlyCollection<string> Roster(string? primaryName, string? primaryPetName,
        IReadOnlyCollection<string>? manualNames) => _roster.Roster(primaryName, primaryPetName, manualNames);

    /// <summary>Every CURRENT roster member's own snapshot, for
    /// <see cref="TeammateCombine.Combine"/>. Only roster members — see
    /// <see cref="Roster"/>'s own doc for why a dropped name is excluded here despite
    /// <see cref="KnownTeammates"/> still holding its stats.</summary>
    public IReadOnlyDictionary<string, StatsSnapshot> Snapshots(string? primaryName, string? primaryPetName,
        IReadOnlyCollection<string>? manualNames)
    {
        var roster = Roster(primaryName, primaryPetName, manualNames);
        var result = new Dictionary<string, StatsSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in roster)
            if (_stats.TryGetValue(name, out var s)) result[name] = s.Snapshot();
        return result;
    }

    /// <summary>Read-only access to one teammate's own live stats, for a per-person
    /// display strip. Never hand out the mutable <see cref="SessionStats"/> itself —
    /// same reasoning as <see cref="ReadOnlyTeammateStats"/> for the file-based
    /// feature.</summary>
    public StatsSnapshot? SnapshotFor(string actor) =>
        _stats.TryGetValue(actor, out var s) ? s.Snapshot() : null;

    /// <summary>Every CURRENT roster member's own LIVE <see cref="SessionStats"/>
    /// instance — for <see cref="TeammateCombine.Combine(SessionStats, IReadOnlyDictionary{string, SessionStats})"/>,
    /// which needs the live spans (a <see cref="StatsSnapshot"/> alone cannot supply
    /// them) to build an exact combat-seconds union, and for tests that need to drive
    /// a teammate's instance directly. Internal, not public: reading spans off it is
    /// safe (see that overload's own doc), but nothing outside this assembly should be
    /// handed a mutable teammate instance to Apply against directly.</summary>
    internal IReadOnlyDictionary<string, SessionStats> LiveStats(string? primaryName, string? primaryPetName,
        IReadOnlyCollection<string>? manualNames)
    {
        var roster = Roster(primaryName, primaryPetName, manualNames);
        var result = new Dictionary<string, SessionStats>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in roster)
            if (_stats.TryGetValue(name, out var s)) result[name] = s;
        return result;
    }

    /// <summary>Clears every teammate's isolated stats and the auto-detected roster —
    /// call this alongside the primary's own session reset (character switch, replay
    /// from byte 0), matching the file-based feature's <c>LogWatcher.Select</c> reset.
    /// Known-NPC exclusions are NOT cleared — see <see cref="TeammateRoster.Reset"/>.</summary>
    public void Reset()
    {
        _roster.Reset();
        _stats.Clear();
    }
}
