namespace EQBuddy.Core;

/// <summary>
/// Holds one isolated <see cref="SessionStats"/> per teammate, fed entirely from the
/// PRIMARY player's own log — there is no second file, no second machine, no synced
/// copy of anything. Every teammate event reaches this class as a line REWRITTEN from
/// a primary log line by <see cref="TeammatePerspective.Rewrite"/> into that teammate's
/// first person, then parsed by the UNCHANGED <see cref="LogParser"/> exactly as if the
/// teammate's own client had printed it.
///
/// <b>Isolation, by construction:</b> a teammate's <see cref="SessionStats"/> here is a
/// plain <c>new SessionStats()</c> — no durable store, no event subscriber, never handed
/// to the archiver, the checkpoint or any ledger. Nothing here can leak a teammate's
/// data into the primary's history, because nothing here is ever given the keys
/// (<c>TeammateIsolationTests</c> proves it by reflection).
///
/// <b>Only what the log actually shows reaches a teammate's stats:</b> melee, spell,
/// DoT and damage-shield damage and misses (both directions), kills, deaths, heals
/// (both directions) and casts. A teammate's XP, AA, loot, coin and faction are never
/// printed in the primary's log, so <see cref="TeammatePerspective"/> never produces
/// them and the combined view keeps those fields the primary's own.
///
/// <b>Lifecycle.</b> The production instance belongs to the primary
/// <see cref="SessionStats"/> (<see cref="SessionStats.Teammates"/>) and is fed by
/// <see cref="LogWatcher"/>'s poll, one call per primary line, AFTER the primary has
/// applied that line. The primary's own session rollover resets every teammate's
/// session with it (<see cref="ResetSession"/>, subscribed in the constructor); a
/// character switch or re-selection resets the roster too (<see cref="Reset"/>).
///
/// <b>Corrections to the primary's party-kill rows are scoped to the PRIMARY's
/// session.</b> The primary's own log files a teammate's kill ("X has been slain by
/// Garg!") and a teammate's death ("Garg has been slain by X!") as party kills. Both
/// are recorded here off the primary's OWN parsed <see cref="KillEvent"/> — the exact
/// target and killer strings the primary bumped — and subtracted exactly by
/// <see cref="TeammateCombine"/>. They are cleared only when the primary's rows are
/// (rollover, re-selection), never when a teammate's own isolated instance rolls: the
/// rows they correct still hold those lines.
///
/// <b>Thread-safety:</b> <see cref="ObservePrimaryLine"/> runs on the poll thread;
/// snapshots and accessors are read from the UI thread while a poll may be in flight.
/// One private <see cref="_gate"/> covers every read and write of the teammates, the
/// corrections and the roster, and every accessor returns a disconnected copy. The
/// primary's own lock is never taken while <see cref="_gate"/> is held.
/// </summary>
public sealed class DerivedTeammates
{
    private readonly object _gate = new();
    private readonly SessionStats? _primary;
    private TeammateRoster _roster = new();
    private readonly Dictionary<string, SessionStats> _stats = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per known teammate, the timestamp of the last event applied to their
    /// instance — the keep-alive tick in <see cref="ObserveCore"/> skips a line stamped
    /// the same second, which changes nothing observable.</summary>
    private readonly Dictionary<string, DateTime> _lastApplied = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Moves on every session end (<see cref="ResetSession"/>, <see cref="Reset"/>),
    /// so a re-derivation that raced one is discarded rather than committed over it.</summary>
    private long _generation;

    /// <summary>Per teammate, the primary's party-kill rows each of THEIR promoted kills
    /// bumped (target and killer, exactly as the primary parsed them).</summary>
    private readonly Dictionary<string, Corrections> _promoted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per player-shaped name, the party-kill rows the primary filed for that
    /// name's DEATH ("Garg has been slain by X!"). Recorded for every such line, whether
    /// or not the name was on the roster yet, and only ever subtracted for a name that
    /// became a teammate — so a death before the group formed is corrected too.</summary>
    private readonly Dictionary<string, Corrections> _deaths = new(StringComparer.OrdinalIgnoreCase);

    private long _version;
    private ManualTeammate[] _manual = [];

    /// <summary>The standalone feed's hand-added names that have joined already — each
    /// joins once, at the first line it is passed with.</summary>
    private readonly HashSet<string> _standaloneJoined = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Who was in the group at the first line of the file the watcher is reading:
    /// nobody after a Select, whoever was detected when the log was truncated under it (a
    /// "Reset session" with archiving on splits the log, and the join line goes with the
    /// archive). A re-derivation starts its roster here.</summary>
    private string[] _membersAtLogStart = [];

    /// <summary>With <see cref="_membersAtLogStart"/>: the last line read before the log was
    /// split. A hand-added join at or before it already shows in those members (or has
    /// already been ended), so a re-derivation of the new file does not place it again.</summary>
    private DateTime? _handAddedPastAtLogStart;

    // Roster cache: rebuilding the whitelist on every one of a long log's lines is the
    // hot path of the initial ingest, and the roster only moves on a handful of lines.
    private string[] _rosterCache = [];
    private (int RosterVersion, string? Primary, string? Pet) _rosterKey = (-1, null, null);

    /// <summary>A standalone instance (tests, tools): the caller feeds it with
    /// <see cref="Observe"/> and resets it explicitly.</summary>
    public DerivedTeammates() { }

    /// <summary>The production instance: <paramref name="primary"/>'s own session
    /// rollover resets the teammates' sessions with it.</summary>
    internal DerivedTeammates(SessionStats primary)
    {
        _primary = primary;
        primary.SessionRolledOver += ResetSession;
    }

    /// <summary>A re-derivation's staging instance: fed from <paramref name="primary"/>'s
    /// log like the production one, but never subscribed to its rollover, and seen by
    /// nobody until <see cref="CommitReplay"/> moves its contents across.</summary>
    private DerivedTeammates(SessionStats primary, TeammateRoster roster, ManualTeammate[] manual)
    {
        _primary = primary;
        _roster = roster;
        _manual = manual;
    }

    /// <summary>The teammates the player added by hand in Options → Behavior
    /// (<see cref="AppSettings.ManualTeammates"/>), every character's. Only the watched
    /// character's apply, and each only as a join at its own moment in the log
    /// (<see cref="TeammateRoster.ArmHandAdded"/>) — never as a standing member. Copied on
    /// set, so a later edit of the caller's list takes effect only when it is set again;
    /// the caller then re-derives (<see cref="LogWatcher.RederiveTeammatesAsync"/>), which
    /// places every join in log order.</summary>
    public IReadOnlyList<ManualTeammate> Manual
    {
        get => Volatile.Read(ref _manual);
        set
        {
            ManualTeammate[] all = value is null ? [] : [.. value.Where(m => m is not null)];
            Volatile.Write(ref _manual, all);
            var joins = JoinsForPrimary(all);   // reads the primary before taking _gate
            lock (_gate) _roster.ArmHandAdded(joins, _roster.LastLine);
        }
    }

    // The watched character's hand-added joins. Reads the primary's name and server, so
    // never call it while holding _gate (the primary's lock is never taken under it).
    private (string Name, DateTime Since)[] JoinsForPrimary(IReadOnlyList<ManualTeammate> all) =>
        _primary is not { } p ? []
            : [.. ManualTeammates.For(all, p.CharacterName, p.ServerName).Select(m => (m.Name, m.Since))];

    /// <summary>When a name the player adds now joins — see <see cref="ManualTeammates.JoinTime"/>:
    /// from the current session's start the first time, from <paramref name="now"/> when
    /// it was added for this character before.</summary>
    public DateTime JoinTimeForHandAdded(string name, DateTime now)
    {
        var addedBefore = _primary is { } p && ManualTeammates.NamesFor(Manual, p.CharacterName, p.ServerName)
            .Contains(name, StringComparer.OrdinalIgnoreCase);
        return ManualTeammates.JoinTime(addedBefore, _primary?.Snapshot().SessionStart, now);
    }

    /// <summary>Every teammate this primary session has applied an event for — a superset
    /// of the CURRENT roster: a name that left the group keeps what it already accrued.</summary>
    public IReadOnlyCollection<string> KnownTeammates { get { lock (_gate) return _stats.Keys.ToList(); } }

    /// <summary>The group members as of the last line read, detected or hand-added — see
    /// <see cref="TeammateRoster.AutoDetected"/>.</summary>
    public IReadOnlyCollection<string> AutoDetected { get { lock (_gate) return _roster.AutoDetected.ToList(); } }

    /// <summary>Moves whenever any teammate's stats move, and on every reset; 0 while no
    /// teammate is known, so the combined version equals the primary's own exactly when
    /// the combined snapshot IS the primary's own.</summary>
    public long Version { get { lock (_gate) return _stats.Count == 0 ? 0 : _version; } }

    /// <summary>The roster this instant — see <see cref="TeammateRoster.Roster"/>.</summary>
    public IReadOnlyCollection<string> Roster(string? primaryName, string? primaryPetName)
    {
        lock (_gate) return _roster.Roster(primaryName, primaryPetName);
    }

    /// <summary>Who is counted right now: <see cref="Roster"/> for the watched character
    /// and their pet — what Options → Behavior shows. Empty for a standalone instance.</summary>
    public IReadOnlyCollection<string> CountedNow()
    {
        if (_primary is not { } p) return [];
        var (name, pet) = (p.CharacterName, p.LivePetName);   // the primary's lock, not under _gate
        return Roster(name, pet);
    }

    /// <summary>The production feed: one PRIMARY log line, after the primary applied
    /// <paramref name="primaryEvent"/> (null when the line parsed to nothing). Reads the
    /// primary's name, pet and pet test before taking this instance's lock.
    ///
    /// <b>Never throws.</b> It runs inside the watcher's poll loop, where an exception
    /// abandons the rest of the chunk — a fault in deriving a teammate must never cost
    /// the player a line of their OWN stats. The first fault is logged; the line is
    /// simply not credited to anybody.</summary>
    public void ObservePrimaryLine(DateTime ts, string msg, GameEvent? primaryEvent)
    {
        var primary = _primary ?? throw new InvalidOperationException(
            "ObservePrimaryLine needs the primary-owned instance (SessionStats.Teammates).");
        try
        {
            var partyKill = primaryEvent is KillEvent k && k.Killer != "You" && !primary.IsMyPet(k.Killer);
            ObserveCore(ts, msg, primaryEvent, partyKill, primary.CharacterName, primary.LivePetName);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _faultLogged, 1) == 0) CoreLog.Error(ex);
        }
    }

    private int _faultLogged;

    /// <summary>The standalone feed: parses <paramref name="msg"/> itself, the way the
    /// primary would. Each name in <paramref name="handAdded"/> joins the group at the first
    /// line it is passed with, once — a join the caller vouches for, not a standing member:
    /// a leave, removal, disband or login line ends it, and passing it again does not bring
    /// it back.</summary>
    public void Observe(DateTime ts, string msg, string? primaryName, string? primaryPetName,
        IReadOnlyCollection<string>? handAdded)
    {
        var evt = LogParser.Parse(ts, msg);
        var partyKill = evt is KillEvent k && k.Killer != "You"
            && !string.Equals(k.Killer, primaryPetName, StringComparison.OrdinalIgnoreCase);
        if (handAdded is not null)
            lock (_gate)
                foreach (var name in handAdded)
                    if (!string.IsNullOrWhiteSpace(name) && _standaloneJoined.Add(name.Trim()))
                        _roster.JoinByHand(name);
        ObserveCore(ts, msg, evt, partyKill, primaryName, primaryPetName);
    }

    private void ObserveCore(DateTime ts, string msg, GameEvent? primaryEvent, bool primaryPartyKill,
        string? primaryName, string? primaryPetName)
    {
        lock (_gate)
        {
            _roster.Observe(ts, msg, primaryName);

            // A player-shaped name's death, as the primary filed it — see _deaths.
            if (primaryPartyKill && primaryEvent is KillEvent death && LooksLikeAPlayer(death.Target))
                Corrections.For(_deaths, death.Target).Add(death.Target, death.Killer);

            var roster = RosterLocked(primaryName, primaryPetName);
            var appliedTo = roster.Length == 0 || !ContainsAnyName(msg, roster)
                ? null
                : ApplyRewrittenLines(ts, msg, primaryName, roster, primaryPartyKill ? primaryEvent as KillEvent : null);

            // Keep every known teammate's session clock moving in step with the
            // primary's own: a line the primary applied an event for ticks each
            // teammate that got nothing of its own from it, so a teammate who is quiet
            // while the primary plays on is never rolled over on their own.
            //
            // A tick stamped the same second as the last event this teammate got is
            // skipped: it could neither roll the session, move the last-event time nor
            // make a fight stale, and one tick per line per teammate was the bulk of
            // what deriving teammates added to a long log's initial ingest.
            if (primaryEvent is not null)
                foreach (var (name, stats) in _stats)
                    if ((appliedTo is null || !appliedTo.Contains(name))
                        && !(_lastApplied.TryGetValue(name, out var last) && last == ts))
                    {
                        stats.Apply(new RawLineEvent(ts, msg));
                        _lastApplied[name] = ts;
                        _version++;
                    }
        }
    }

    // Caller holds _gate.
    private HashSet<string> ApplyRewrittenLines(DateTime ts, string msg, string? primaryName,
        string[] roster, KillEvent? primaryPartyKill)
    {
        var appliedTo = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in TeammatePerspective.Rewrite(msg, primaryName ?? "", roster))
        {
            var evt = LogParser.Parse(ts, line.Line);
            if (evt is null) continue;
            // Fold a teammate's owned pet in as one of THEIR rows, tagged "(pet)" on
            // whatever field names the ability, rather than a same-named row that would
            // silently merge the pet's swing into the owner's own. Kills/deaths/misses
            // carry no such field and pass through unchanged — a pet's kill counts as
            // the owner's kill, as the primary's own pet kills already do.
            if (line.IsPet) evt = TagPet(evt);
            GetOrCreate(line.Actor).Apply(evt);
            _lastApplied[line.Actor] = ts;
            _version++;
            appliedTo.Add(line.Actor);
            // The same line was a party kill in the primary's own rows: remember exactly
            // which rows, so the combine can move this kill out of them.
            if (evt is KillEvent { Killer: "You" } && primaryPartyKill is { } pk)
                Corrections.For(_promoted, line.Actor).Add(pk.Target, pk.Killer);
        }
        return appliedTo;
    }

    /// <summary>A pet's hit is damage the owner dealt, but not a swing the owner took:
    /// <c>IsAux</c> keeps it out of the hit, crit and special-hit counters and the spell
    /// classification, proc and burst paths — exactly what the primary's own
    /// <c>AddPetDamage</c> keeps its pet out of — while it still reaches DamageDealt, the
    /// timeline, combat and its tagged ability row. A pet's miss is not credited to the
    /// owner's accuracy either (the primary's own pet misses are not): it becomes a
    /// third-party miss, which only keeps an open combat window going.</summary>
    private static GameEvent TagPet(GameEvent evt) => evt switch
    {
        DamageDealtEvent d => d with { Source = TagName(d.Source), IsAux = true },
        HealEvent h => h with { Spell = TagName(h.Spell) },
        MissEvent m => new ThirdMissEvent(m.Time, TagName("")),
        _ => evt,
    };

    private static string TagName(string name) => name.Length == 0 ? "(pet)" : $"{name} (pet)";

    // Caller holds _gate.
    private SessionStats GetOrCreate(string actor)
    {
        if (_stats.TryGetValue(actor, out var s)) return s;
        s = new SessionStats { CharacterName = actor };
        _stats[actor] = s;
        return s;
    }

    // Caller holds _gate.
    private string[] RosterLocked(string? primaryName, string? primaryPetName)
    {
        var key = (_roster.Version, primaryName, primaryPetName);
        if (_rosterKey.RosterVersion == key.Version
            && string.Equals(_rosterKey.Primary, primaryName, StringComparison.Ordinal)
            && string.Equals(_rosterKey.Pet, primaryPetName, StringComparison.Ordinal))
            return _rosterCache;
        _rosterCache = [.. _roster.Roster(primaryName, primaryPetName)];
        _rosterKey = key;
        return _rosterCache;
    }

    /// <summary>Cheap pre-filter: the rewrite only ever matches a line that names a
    /// roster member, and on a long replay almost no line does.</summary>
    private static bool ContainsAnyName(string msg, string[] roster)
    {
        foreach (var name in roster)
            if (msg.Contains(name, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>A single capitalised word — the only shape an EverQuest character name
    /// has. Creatures carry an article or several words; a single-word NPC recorded here
    /// is harmless, since only a name that becomes a teammate is ever subtracted.</summary>
    private static bool LooksLikeAPlayer(string name) =>
        name.Length > 1 && char.IsUpper(name[0]) && name.All(char.IsLetter);

    /// <summary>Every known teammate's own snapshot (no window, no rules) — for a
    /// per-person display and for tests.</summary>
    public IReadOnlyDictionary<string, StatsSnapshot> Snapshots()
    {
        lock (_gate)
        {
            var result = new Dictionary<string, StatsSnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, s) in _stats) result[name] = s.Snapshot();
            return result;
        }
    }

    /// <summary>Every known teammate's own LIVE instance — for tests that inspect the
    /// instance itself (the isolation guard). Internal: nothing outside this assembly is
    /// handed a mutable teammate instance.</summary>
    internal IReadOnlyDictionary<string, SessionStats> LiveStats()
    {
        lock (_gate) return new Dictionary<string, SessionStats>(_stats, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The killers the primary's log named for <paramref name="name"/>'s deaths
    /// this primary session. A disconnected copy; empty, never null.</summary>
    public IReadOnlyDictionary<string, int> DeathKillersFor(string name)
    {
        lock (_gate)
            return _deaths.TryGetValue(name, out var c)
                ? new Dictionary<string, int>(c.ByKiller, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The latest event time across every known teammate — the combine anchors
    /// its shared recent window on the later of this and the primary's own.</summary>
    internal DateTime? LastEventTime
    {
        get
        {
            lock (_gate)
            {
                DateTime? latest = null;
                foreach (var s in _stats.Values)
                    if (s.LastEventTimeSnapshot is { } t && (latest is null || t > latest)) latest = t;
                return latest;
            }
        }
    }

    /// <summary>One consistent capture of every known teammate, for
    /// <see cref="TeammateCombine"/>: each side's snapshot and spans, the party-kill
    /// rows to move out of the primary's, and the version it all reflects.</summary>
    internal (IReadOnlyList<TeammateCombine.Mate> Mates, long Version) CaptureForCombine(
        TimeSpan? recentWindow, DateTime? windowEnd, DateTime now)
    {
        lock (_gate)
        {
            var mates = new List<TeammateCombine.Mate>(_stats.Count);
            foreach (var (name, stats) in _stats.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                var byTarget = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var byKiller = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                if (_promoted.TryGetValue(name, out var kills)) kills.AddTo(byTarget, byKiller);
                if (_deaths.TryGetValue(name, out var deaths)) deaths.AddTo(byTarget, byKiller);
                mates.Add(new TeammateCombine.Mate(name,
                    stats.CaptureForCombine(recentWindow, null, windowEnd, now), byTarget, byKiller));
            }
            return (mates, _stats.Count == 0 ? 0 : _version);
        }
    }

    /// <summary>The primary's session rolled over: every teammate's session, and every
    /// correction to the primary's party-kill rows, ends with it. The roster is kept —
    /// a quiet hour does not change who is in the group, and group lines are not
    /// repeated when play resumes. What does end membership, a logout, is a line of its
    /// own ("Welcome to EverQuest…" at the next login), which the roster answers in log
    /// order (<see cref="TeammateRoster.Observe"/>).</summary>
    public void ResetSession()
    {
        lock (_gate)
        {
            _stats.Clear();
            _lastApplied.Clear();
            _promoted.Clear();
            _deaths.Clear();
            _generation++;
            _version++;
        }
    }

    /// <summary>A character switch or a re-selection of the log: everything above, plus
    /// the roster (the replay that follows re-detects it, and places the NEW watched
    /// character's hand-added joins in order). Known-NPC exclusions are kept — see
    /// <see cref="TeammateRoster.Reset"/>.</summary>
    public void Reset()
    {
        var joins = JoinsForPrimary(Manual);
        lock (_gate)
        {
            _roster.Reset();
            _roster.ArmHandAdded(joins, null);
            _standaloneJoined.Clear();
            _membersAtLogStart = [];
            _handAddedPastAtLogStart = null;
            _stats.Clear();
            _lastApplied.Clear();
            _promoted.Clear();
            _deaths.Clear();
            _generation++;
            _version++;
        }
    }

    /// <summary>Starts a re-derivation of the current session: a staging instance with
    /// the hand-added joins as they are now and a roster that knows only the members at the
    /// start of the file (none, unless the log was split under the watcher — see
    /// <see cref="LogRestarted"/>; the known-NPC exclusions are kept, as every reset keeps them), plus the generation it
    /// must still match to be committed. The caller feeds the staging instance every line
    /// since the log was selected — <see cref="ObserveRosterLine"/> before the session
    /// start, so join and leave lines rebuild membership in log order, and
    /// <see cref="ObservePrimaryLine"/> from it — then calls <see cref="CommitReplay"/>.</summary>
    internal (DerivedTeammates Staging, long Generation) BeginReplay()
    {
        var primary = _primary ?? throw new InvalidOperationException(
            "BeginReplay needs the primary-owned instance (SessionStats.Teammates).");
        var manual = Manual as ManualTeammate[] ?? [.. Manual];
        var joins = JoinsForPrimary(manual);
        lock (_gate)
            return (new DerivedTeammates(primary,
                _roster.WithoutMembers(_membersAtLogStart, joins, _handAddedPastAtLogStart), manual), _generation);
    }

    /// <summary>The watched file was truncated and is being read again from its first byte:
    /// the members detected now are the members at that first byte, so a re-derivation from
    /// it starts with them rather than with nobody.</summary>
    internal void LogRestarted()
    {
        lock (_gate)
        {
            _membersAtLogStart = [.. _roster.AutoDetected];
            _handAddedPastAtLogStart = _roster.LastLine;
        }
    }

    /// <summary>A line from before the current session: only its group lines (and the
    /// hand-added joins due by its time) matter — whatever it credited was cleared when
    /// the session rolled.</summary>
    internal void ObserveRosterLine(DateTime ts, string msg)
    {
        var primaryName = _primary?.CharacterName;
        lock (_gate) _roster.Observe(ts, msg, primaryName);
    }

    /// <summary>Replaces this instance's teammates, corrections and roster with what
    /// <paramref name="staging"/> derived, in one step under the lock and with one version
    /// bump, so no reader ever sees a half-replayed session. Refused (false) when a session
    /// ended since <see cref="BeginReplay"/>: the replay described a session that is gone.</summary>
    internal bool CommitReplay(DerivedTeammates staging, long generation)
    {
        lock (_gate)
        {
            if (generation != _generation) return false;
            lock (staging._gate)
            {
                _roster = staging._roster;
                _rosterKey = (-1, null, null);
                Replace(_stats, staging._stats);
                Replace(_lastApplied, staging._lastApplied);
                Replace(_promoted, staging._promoted);
                Replace(_deaths, staging._deaths);
            }
            _version++;
            return true;
        }

        static void Replace<T>(Dictionary<string, T> into, Dictionary<string, T> from)
        {
            into.Clear();
            foreach (var (k, v) in from) into[k] = v;
        }
    }

    /// <summary>Counts of primary party-kill rows, by target and by killer, that one
    /// person's events account for.</summary>
    private sealed class Corrections
    {
        public Dictionary<string, int> ByTarget { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> ByKiller { get; } = new(StringComparer.OrdinalIgnoreCase);

        public static Corrections For(Dictionary<string, Corrections> map, string name)
        {
            if (!map.TryGetValue(name, out var c)) map[name] = c = new Corrections();
            return c;
        }

        public void Add(string target, string killer)
        {
            ByTarget[target] = ByTarget.GetValueOrDefault(target) + 1;
            ByKiller[killer] = ByKiller.GetValueOrDefault(killer) + 1;
        }

        public void AddTo(Dictionary<string, int> byTarget, Dictionary<string, int> byKiller)
        {
            foreach (var (k, v) in ByTarget) byTarget[k] = byTarget.GetValueOrDefault(k) + v;
            foreach (var (k, v) in ByKiller) byKiller[k] = byKiller.GetValueOrDefault(k) + v;
        }
    }
}
