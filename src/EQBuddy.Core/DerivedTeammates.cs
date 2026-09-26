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
///
/// <b>Thread-safety (audit finding):</b> <see cref="Observe"/> runs on the poll thread;
/// <see cref="Snapshots"/>/<see cref="LiveStats"/>/<see cref="KnownTeammates"/>/
/// <see cref="AutoDetected"/> are read from the UI thread while a poll may be in
/// flight. One private <see cref="_gate"/> covers every read and write of
/// <see cref="_stats"/>, <see cref="_deathKillers"/> and the roster; every accessor
/// below returns a disconnected copy rather than a live collection, so a UI-thread
/// enumeration can never race a poll-thread mutation of the same object.
/// </summary>
public sealed class DerivedTeammates
{
    private readonly object _gate = new();
    private readonly TeammateRoster _roster = new();
    private readonly Dictionary<string, SessionStats> _stats = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per teammate, how many of THEIR OWN deaths this session were an exact
    /// "&lt;actor&gt; has been slain by &lt;killer&gt;!" line — never a killer-less
    /// "&lt;actor&gt; died." (which the primary's own log never turns into a
    /// <see cref="KillEvent"/> at all, so there is nothing to correct there) and never
    /// upstream's own damage-fallback guess for a killer-less death (that guess lives
    /// only inside the teammate's OWN isolated <see cref="SessionStats"/>, via its
    /// ordinary <see cref="DeathEvent"/> handling, and is deliberately not mirrored
    /// here). Captured from the exact parsed <see cref="DeathEvent"/> BEFORE it reaches
    /// <see cref="SessionStats.Apply"/>, so it can never inherit that fallback — see
    /// <see cref="TeammateCombine"/>'s own doc for why this pairing is exact.</summary>
    private readonly Dictionary<string, Dictionary<string, int>> _deathKillers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Audit finding: the teammate's own <c>SessionStats</c> rolls over on a
    /// 60-minute gap independently of <see cref="Reset"/> (a character switch), and
    /// <see cref="_deathKillers"/> used to be cleared only by the latter — so a death
    /// from an earlier SESSION survived a rollover and was later subtracted from a
    /// fresh session's <c>PartyKillsByKiller</c> row for the same killer name, deleting
    /// real party kills the new session actually earned. Tracks each teammate's last
    /// observed <see cref="SessionStats.SessionStartSnapshot"/>; a change (checked
    /// before every death is recorded) means that teammate's instance rolled over
    /// since the last death was captured, so their stale death-killer counts are
    /// dropped first.</summary>
    private readonly Dictionary<string, DateTime?> _deathKillerSessionStart =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every teammate this session has ever applied an event for — a superset
    /// of the CURRENT roster, since a name dropped from the roster (an exclusion
    /// learned later, or removed from the manual list) still owns whatever isolated
    /// stats it already accrued rather than losing them retroactively. A disconnected
    /// copy — see the class doc's thread-safety note.</summary>
    public IReadOnlyCollection<string> KnownTeammates { get { lock (_gate) return _stats.Keys.ToList(); } }

    /// <summary>Names auto-detected from group join/invite/tell lines so far — see
    /// <see cref="TeammateRoster.AutoDetected"/>. A disconnected copy — see the class
    /// doc's thread-safety note.</summary>
    public IReadOnlyCollection<string> AutoDetected { get { lock (_gate) return _roster.AutoDetected.ToList(); } }

    /// <summary>Feed one primary-log line. <paramref name="primaryName"/> and
    /// <paramref name="primaryPetName"/> are read fresh on every call (a pet can be
    /// resummoned mid-session under a new name) rather than captured once. Cheap when
    /// the roster is empty — the auto-detector still runs (it is what LETS the roster
    /// stop being empty), but <see cref="TeammatePerspective.Rewrite"/> is never called
    /// against zero candidate names.
    ///
    /// <b>Every already-known teammate's own session clock is kept moving in lockstep
    /// with the primary's, but ONLY on a line the primary itself would apply an event
    /// for</b> — exactly upstream's own <see cref="SessionStats.ObserveRawLine"/> rule
    /// (a raw line only advances the rollover clock when it matches a configured Text
    /// watch rule; ordinary chat never does), approximated here by
    /// <c>LogParser.Parse(ts, msg) is not null</c>. Before this gate, EVERY raw line —
    /// chat included — ticked every known teammate's clock, so a teammate stayed
    /// "fresh" through a genuine, multi-hour, event-silent gap (guild chat scrolling
    /// while AFK, no combat) that correctly rolls the PRIMARY's own session over on its
    /// next real event: the primary rolled, the just-ticked teammate did not, and the
    /// teammate's stale total from the OLD session was folded into the combined view
    /// alongside the primary's fresh one. Gating on a real parsed event keeps a
    /// teammate's clock alive for exactly as long as the primary's genuinely would be —
    /// a teammate silent while the PRIMARY plays on (real events, not just raw lines)
    /// still never loses ground (see <see cref="ApplyRewrittenLines"/>'s own per-line
    /// event application below for the ordinary case: a line naming the teammate
    /// applies a real event to THEIR instance directly, which is what actually keeps
    /// them fresh during active co-op play; this raw tick only covers the primary's OWN
    /// events, which a teammate's instance would otherwise never see at all).
    ///
    /// A teammate who DID receive a real rewritten event from this line is skipped (no
    /// point ticking twice for one line), and the primary's own multi-hour event-silent
    /// gaps still roll every teammate over in step, because the tick's timestamp is the
    /// same one that would trigger the primary's own rollover.</summary>
    public void Observe(DateTime ts, string msg, string? primaryName, string? primaryPetName,
        IReadOnlyCollection<string>? manualNames)
    {
        lock (_gate)
        {
            _roster.Observe(msg, primaryName);
            var roster = _roster.Roster(primaryName, primaryPetName, manualNames);

            var appliedTo = roster.Count == 0 ? null : ApplyRewrittenLines(ts, msg, primaryName, roster);

            if (LogParser.Parse(ts, msg) is not null)
                foreach (var (name, stats) in _stats)
                    if (appliedTo is null || !appliedTo.Contains(name))
                        stats.Apply(new RawLineEvent(ts, msg));
        }
    }

    // Callers all hold _gate already.
    private HashSet<string> ApplyRewrittenLines(DateTime ts, string msg, string? primaryName,
        IReadOnlyCollection<string> roster)
    {
        var appliedTo = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in TeammatePerspective.Rewrite(msg, primaryName ?? "", roster))
        {
            var evt = LogParser.Parse(ts, line.Line);
            if (evt is null) continue;
            // Audit finding: this actor's own SessionStats may have rolled over (a
            // 60-minute gap) since the last death was recorded for them — drop any
            // death-killer counts from the session that just ended before adding to
            // (or reading) this one. Checked on every line, not only death lines, so
            // a rollover is caught even when the very next event isn't itself a death.
            var actorStats = GetOrCreate(line.Actor);
            var actorSessionStart = actorStats.SessionStartSnapshot;
            if (_deathKillerSessionStart.TryGetValue(line.Actor, out var knownStart) && knownStart != actorSessionStart)
                _deathKillers.Remove(line.Actor);
            _deathKillerSessionStart[line.Actor] = actorSessionStart;
            // Finding: the pairing needed to correct a teammate death's misfiled
            // killer-row (see TeammateCombine) is exact ONLY when captured here, off
            // the just-parsed event itself, before TagPet/Apply ever touch it — a
            // non-empty Killer means this line was literally "<actor> has been slain
            // by <killer>!"; upstream's own damage-fallback guess for a killer-less
            // "<actor> died." lives inside Apply and must never be mirrored into this
            // count (a "died." line has no primary-side KillEvent to correct at all).
            if (evt is DeathEvent { Killer.Length: > 0 } death)
            {
                var byKiller = _deathKillers.TryGetValue(line.Actor, out var d)
                    ? d : _deathKillers[line.Actor] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                byKiller[death.Killer] = byKiller.GetValueOrDefault(death.Killer) + 1;
            }
            // Fold a teammate's owned pet in as one of THEIR rows, tagged "(pet)" on
            // whatever field names the ability, rather than a same-named row that
            // would silently merge the pet's swing into the owner's own (design
            // survey §3: "fold into the owner's numbers with a (pet) skill row").
            // Kills/deaths/misses carry no such field and pass through unchanged —
            // a pet's kill still counts as the owner's kill, matching how upstream
            // already folds the PRIMARY's own pet kills into YourKillCount.
            if (line.IsPet) evt = TagPet(evt);
            actorStats.Apply(evt);
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

    // Caller holds _gate already.
    private SessionStats GetOrCreate(string actor)
    {
        if (_stats.TryGetValue(actor, out var s)) return s;
        s = new SessionStats { CharacterName = actor };
        _stats[actor] = s;
        return s;
    }

    /// <summary>The roster this instant — see <see cref="TeammateRoster.Roster"/>.
    /// Exposed for Options → Behavior (detected names shown, add/remove) and to decide
    /// whether a NEW line gets rewritten (<see cref="ApplyRewrittenLines"/>) — NOT for
    /// the combine step, which folds in every <see cref="KnownTeammates"/> entry
    /// regardless of current roster membership; see <see cref="Snapshots"/>'s own doc
    /// for why.</summary>
    public IReadOnlyCollection<string> Roster(string? primaryName, string? primaryPetName,
        IReadOnlyCollection<string>? manualNames)
    {
        lock (_gate) return _roster.Roster(primaryName, primaryPetName, manualNames);
    }

    /// <summary>Every KNOWN teammate's own snapshot, for <see cref="TeammateCombine.Combine(StatsSnapshot, IReadOnlyDictionary{string, StatsSnapshot}, IReadOnlyDictionary{string, IReadOnlyDictionary{string, int}})"/>.
    ///
    /// <b>Folds in every name <see cref="KnownTeammates"/> holds, not only the CURRENT
    /// roster</b> (audit finding): a name dropped from the roster — left the group,
    /// was removed, the group disbanded — used to disappear from the combined view
    /// entirely, including every hit already on screen from earlier in the same
    /// session, which contradicts <see cref="TeammateRoster"/>'s own documented
    /// invariant ("a name dropped here does not lose the stats it already accrued").
    /// The roster is consulted only to decide whether a NEW line gets rewritten for a
    /// name going forward — never to decide whether an EXISTING contributor still
    /// counts.</summary>
    public IReadOnlyDictionary<string, StatsSnapshot> Snapshots(string? primaryName, string? primaryPetName,
        IReadOnlyCollection<string>? manualNames)
    {
        lock (_gate)
        {
            var result = new Dictionary<string, StatsSnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, s) in _stats) result[name] = s.Snapshot();
            return result;
        }
    }

    /// <summary>Read-only access to one teammate's own live stats, for a per-person
    /// display strip. Never hand out the mutable <see cref="SessionStats"/> itself —
    /// same reasoning as <see cref="ReadOnlyTeammateStats"/> for the file-based
    /// feature.</summary>
    public StatsSnapshot? SnapshotFor(string actor)
    {
        lock (_gate) return _stats.TryGetValue(actor, out var s) ? s.Snapshot() : null;
    }

    /// <summary>Per-killer death counts for one KNOWN teammate — see
    /// <see cref="_deathKillers"/>'s own doc. Empty, never null, when the teammate has
    /// no recorded "slain by" death this session. A disconnected copy.</summary>
    public IReadOnlyDictionary<string, int> DeathKillersFor(string actor)
    {
        lock (_gate)
            return _deathKillers.TryGetValue(actor, out var d)
                ? new Dictionary<string, int>(d, StringComparer.OrdinalIgnoreCase)
                : EmptyKillers;
    }

    private static readonly IReadOnlyDictionary<string, int> EmptyKillers =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every KNOWN teammate's own LIVE <see cref="SessionStats"/> instance —
    /// for <see cref="TeammateCombine.Combine(SessionStats, IReadOnlyDictionary{string, SessionStats}, IReadOnlyDictionary{string, IReadOnlyDictionary{string, int}})"/>,
    /// which needs the live spans (a <see cref="StatsSnapshot"/> alone cannot supply
    /// them) to build an exact combat-seconds union, and for tests that need to drive
    /// a teammate's instance directly. Internal, not public: reading spans off it is
    /// safe (see that overload's own doc), but nothing outside this assembly should be
    /// handed a mutable teammate instance to Apply against directly. Same "every KNOWN
    /// teammate, not only the current roster" rule as <see cref="Snapshots"/> — see its
    /// doc.</summary>
    internal IReadOnlyDictionary<string, SessionStats> LiveStats(string? primaryName, string? primaryPetName,
        IReadOnlyCollection<string>? manualNames)
    {
        lock (_gate) return new Dictionary<string, SessionStats>(_stats, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Clears every teammate's isolated stats, death-killer counts and the
    /// auto-detected roster — call this alongside the primary's own session reset
    /// (character switch, replay from byte 0), matching the file-based feature's
    /// <c>LogWatcher.Select</c> reset. Known-NPC exclusions are NOT cleared — see
    /// <see cref="TeammateRoster.Reset"/>.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _roster.Reset();
            _stats.Clear();
            _deathKillers.Clear();
            _deathKillerSessionStart.Clear();
        }
    }
}
