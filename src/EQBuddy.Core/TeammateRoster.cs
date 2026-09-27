using System.Text.RegularExpressions;

namespace EQBuddy.Core;

/// <summary>
/// Auto-detects group membership from the PRIMARY player's own log, so a duo/group
/// doesn't have to hand-type every teammate's name into settings. Pure incremental
/// state, fed one raw (already-normalized) message at a time — no file I/O, no
/// durable store, nothing persisted beyond the in-memory session this instance lives
/// for.
///
/// <b>A name the player added by hand is a join, not a standing member.</b> Each one
/// (<see cref="ManualTeammate"/>) joins at its own moment in the log — the start of the
/// session it was added in — and from then on is a member like any other: the same leave,
/// removal, disband and login lines end it (<see cref="ArmHandAdded"/>). It used to be
/// unioned into <see cref="Roster"/> unconditionally, from a global list, so a name added
/// once for one evening was whitelisted on every later day and on every other character.
///
/// <b>Signals used (from the design survey):</b> join/leave/invite lines, "X tells the
/// group", and "Targeted (NPC): X" / "X told you, '...'" (exclusions, never inclusions)
/// are exact string shapes. The fourth, kill-plus-party-XP correlation, covers a member no
/// group line names: someone already in the group when you joined it, or a group whose
/// join line a "Reset session" moved to Logsrchive. See <see cref="PartyKillsToJoin"/>.
///
/// <b>Membership ends at login, unless the group is still there.</b> Camping normally
/// drops you from the group: in the player's real log, 43 of 45 logins are followed by a
/// fresh invite/join (or no party XP at all) before the next "You gain party experience".
/// The other two were quick relogs the group survived. So a login moves the detected
/// members aside, and that login's first party-XP line — before any group line — puts them
/// back; the next login, or any group line, drops them for good.
///
/// <b>Whitelist only, exact-word match:</b> a bystander PC is never counted just for
/// appearing in the log — <see cref="TeammatePerspective.Rewrite"/> (which does the
/// actual line rewriting) already refuses anything outside the roster this class (plus
/// the user's manual list) produces, and matches whole words only, so "Garg" can never
/// match "Gargoyle".
/// </summary>
public sealed class TeammateRoster
{
    private static readonly Regex JoinedGroupRx = new(
        @"^(?<name>[A-Za-z]+) has joined the group\.$", RegexOptions.Compiled);
    // An invite alone is NOT membership — it is only ever promoted once the primary
    // confirms it, below. See Observe's own doc for why "invites you" was dropped
    // from the add list (a declined/ignored invite used to promote the inviter
    // permanently — a real bystander's kills and damage counted as the "your" and
    // party totals for the rest of the session).
    private static readonly Regex AgreedToJoinRx = new(
        @"^You notify (?<name>[A-Za-z]+) that you agree to join the group\.$", RegexOptions.Compiled);
    private static readonly Regex TellsGroupRx = new(
        @"^(?<name>[A-Za-z]+) tells the group,", RegexOptions.Compiled);
    private static readonly Regex TargetedNpcRx = new(
        @"^Targeted \(NPC\): (?<name>.+)$", RegexOptions.Compiled);
    private static readonly Regex LeftGroupRx = new(
        @"^(?<name>[A-Za-z]+) has left the group\.$", RegexOptions.Compiled);
    private static readonly Regex RemovedFromPartyRx = new(
        @"^You remove (?<name>[A-Za-z]+) from the party\.$", RegexOptions.Compiled);
    private static readonly Regex UserRemovedRx = new(
        @"^You have been removed from the group\.$", RegexOptions.Compiled);
    private static readonly Regex UserLeftOrDisbandedRx = new(
        @"^(?:You have left the group\.|Your group has been disbanded\.?)$", RegexOptions.Compiled);

    private static readonly Regex SlainByNameRx = new(
        @"^.+ has been slain by (?<name>[A-Z][a-z]+)!$", RegexOptions.Compiled);
    // Players "tell" you; merchants, trainers and pets (the player's own "Attacking a bat
    // Master.") "told" you — no groupmate in the real log ever did.
    private static readonly Regex NpcToldYouRx = new(
        @"^(?<name>[A-Za-z]+) told you, '", RegexOptions.Compiled);
    private const string LoginPrefix = "Welcome to EverQuest";
    private const string PartyXpPrefix = "You gain party experience";

    /// <summary>Kill-plus-party-XP correlation: a single-word name that lands a kill
    /// ("A gnoll has been slain by Garg!") right after "You gain party experience" — the
    /// client prints the XP line first — this many times since the last login joins the
    /// roster. Measured on the player's 850,000-line log with the NPC exclusions below in
    /// force: at 3, Garg, Kellisanth, Yungweezy and Ripto are detected and nobody else;
    /// no bystander ever correlated more than once, and the only name at 2 that was not
    /// already excluded (Konobtik, a pet) is excluded at 3.</summary>
    internal const int PartyKillsToJoin = 3;

    /// <summary>How many lines after a party-XP line its kill line may come. Any kill
    /// line closes the window, so one XP line never vouches for two kills.</summary>
    private const int PartyXpWindowLines = 3;

    private readonly HashSet<string> _autoDetected = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _partyXpKills = new(StringComparer.OrdinalIgnoreCase);
    private int _partyXpLinesLeft;

    /// <summary>The hand-added joins for the watched character, oldest first, and how many
    /// of them are behind the reader (joined, or already in the past when armed).</summary>
    private (string Name, DateTime Since)[] _handAdded = [];
    private int _handAddedPassed;

    /// <summary>The time of the last line observed since the roster was armed or reset —
    /// null before the first. A hand-added join older than the first line read is not
    /// placed at that line: the log from its moment is not being read (a split log, review
    /// mode), so whether the group still held is not known, and a whitelist only admits
    /// what it knows.</summary>
    internal DateTime? LastLine { get; private set; }

    /// <summary>The members detected when the player last logged in, held aside until
    /// that login's first party XP (the group survived: they come back) or its first group
    /// line or the next login (it did not: they are dropped).</summary>
    private string[] _beforeLogin = [];

    /// <summary>Every name this session has ever seen the client label an NPC ("Targeted
    /// (NPC): X", or "X told you, '...'", which only NPCs and pets say) — a
    /// whitelist member is refused permanently once its name shows up here, since the
    /// same string can never be trusted as a player again this session (design survey
    /// §F: "the risk comes from generic single-word names"). Never cleared by
    /// <see cref="Reset"/> — an NPC identity does not become a player identity when the
    /// session rolls, so the exclusion should outlive it; the cost of a name staying
    /// excluded across a reset is a manual re-add, the cost of losing the exclusion is
    /// counting a mob as a teammate.</summary>
    private readonly HashSet<string> _everNpc = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The group members as of the last line read: detected from the log (group
    /// joins/invites/tells, party-XP kills) or joined from the hand-added list. Exposed
    /// read-only so Options → Behavior can show who is counted now — never itself the
    /// roster; see <see cref="Roster"/> for exclusions.</summary>
    public IReadOnlyCollection<string> AutoDetected => _autoDetected;

    /// <summary>Moves whenever the auto-detected set or the known-NPC set changes, so a
    /// caller can cache <see cref="Roster"/> between the rare lines that move it.</summary>
    internal int Version { get; private set; }

    /// <summary>Feed one already-split (timestamp stripped) log message. Cheap early-out:
    /// every pattern here anchors at the start of the line, so a single ordinal
    /// starts-with/contains check per pattern is enough before the regex runs.
    ///
    /// Membership tracks who is in the group NOW, not everyone who ever appeared to
    /// be: a name is added on a real join (an accepted invite or the join line), group
    /// chat or <see cref="PartyKillsToJoin"/> party-XP kills, and removed on that person
    /// leaving, being removed, the group disbanding, or the player logging in (held
    /// aside for that login's first party XP — see the class doc). A name dropped here does not
    /// lose the stats it already accrued: <see cref="DerivedTeammates.KnownTeammates"/>
    /// keeps every name this session has ever applied an event for regardless of
    /// whether <see cref="Roster"/> still lists them.
    ///
    /// <paramref name="primaryName"/> disambiguates <see cref="RemovedFromPartyRx"/>:
    /// the client logs the PRIMARY's own `/disband` as "You remove &lt;YourName&gt;
    /// from the party." — the exact same shape as removing a groupmate, with the
    /// primary's own name in the slot. Without checking for it, that line read as
    /// "remove a member named `Smargush`" (never in the set, so a no-op) instead of
    /// "the primary left/disbanded", and the roster was never cleared — a real,
    /// observed log line (finding: 2026-08-29 `You remove Smargush from the
    /// party.`), not a hypothetical.</summary>
    public void Observe(DateTime ts, string msg, string? primaryName)
    {
        JoinHandAddedThrough(ts);
        Observe(msg, primaryName);
    }

    /// <summary>The name THIS call to <see cref="Observe(string, string?)"/> promoted
    /// onto the roster via the kill-plus-party-XP correlation (<see cref="CreditPartyKill"/>)
    /// — null on every other call, including one that promotes a name by a group line or
    /// a hand-added join. Kill-correlation is the one join mechanism whose OWN evidence
    /// line (the kill) carries no data about the earlier hit that actually finished the
    /// mob — see <see cref="DerivedTeammates.ReplayBufferedLinesFor"/>, the only reader.
    /// Deliberately narrow: a group-line join or a hand-added join needs no retroactive
    /// replay (the join line itself never carries damage), and scoping this to the one
    /// mechanism that does keeps a member leaving and later rejoining from having a
    /// leave-to-rejoin quiet period wrongly replayed.</summary>
    internal string? LastPartyKillPromotion { get; private set; }

    /// <summary><see cref="Observe(DateTime, string, string?)"/> without a timestamp: no
    /// hand-added join is placed. For callers that feed group lines alone.</summary>
    public void Observe(string msg, string? primaryName)
    {
        LastPartyKillPromotion = null;
        if (string.IsNullOrEmpty(msg)) return;
        var partyXpJustBefore = _partyXpLinesLeft > 0;
        if (partyXpJustBefore) _partyXpLinesLeft--;

        if (msg.StartsWith(PartyXpPrefix, StringComparison.Ordinal))
        {
            _partyXpLinesLeft = PartyXpWindowLines;
            if (_beforeLogin.Length > 0) { _autoDetected.UnionWith(_beforeLogin); _beforeLogin = []; Version++; }
            return;
        }
        if (msg.StartsWith(LoginPrefix, StringComparison.Ordinal))
        {
            _beforeLogin = [.. _autoDetected];
            _autoDetected.Clear();
            _partyXpKills.Clear();
            _partyXpLinesLeft = 0;
            Version++;
            return;
        }
        if (msg[^1] == '!' && (msg.StartsWith("You have slain ", StringComparison.Ordinal)
                               || msg.Contains(" has been slain by ", StringComparison.Ordinal)))
        {
            _partyXpLinesLeft = 0;
            if (partyXpJustBefore && SlainByNameRx.Match(msg) is { Success: true } kill)
                CreditPartyKill(kill.Groups["name"].Value, primaryName);
            return;
        }
        if (msg.Contains(" told you, '", StringComparison.Ordinal))
        {
            if (NpcToldYouRx.Match(msg) is { Success: true } npc && _everNpc.Add(npc.Groups["name"].Value)) Version++;
            return;
        }

        // Every shape below names the group or the party, or is a Targeted line — one
        // vectorised scan skips the regexes on the other ~99.9% of a long replay.
        if (!msg.Contains("group", StringComparison.Ordinal) && !msg.Contains(" party.", StringComparison.Ordinal)
            && !msg.StartsWith("Targeted (NPC): ", StringComparison.Ordinal))
            return;

        Match m;
        if ((m = JoinedGroupRx.Match(msg)).Success) Join(m.Groups["name"].Value);
        else if ((m = AgreedToJoinRx.Match(msg)).Success) Join(m.Groups["name"].Value);
        else if ((m = TellsGroupRx.Match(msg)).Success) _autoDetected.Add(m.Groups["name"].Value);
        else if ((m = LeftGroupRx.Match(msg)).Success) Leave(m.Groups["name"].Value);
        else if ((m = RemovedFromPartyRx.Match(msg)).Success)
        {
            var removed = m.Groups["name"].Value;
            if (primaryName is { Length: > 0 } && string.Equals(removed, primaryName, StringComparison.Ordinal))
                GroupEnded();
            else
                Leave(removed);
        }
        else if (UserRemovedRx.IsMatch(msg) || UserLeftOrDisbandedRx.IsMatch(msg)) GroupEnded();
        else if (msg[0] == 'T' && (m = TargetedNpcRx.Match(msg)).Success) _everNpc.Add(m.Groups["name"].Value);
        else return;
        // A matched line may still change nothing (a repeated join), but a membership
        // swap of equal size would not move either count — so any matched line bumps.
        Version++;
    }

    // A group line after a login announces the group afresh: whoever was set aside at the
    // login is no longer assumed to be in it.
    private void Join(string name)
    {
        _beforeLogin = [];
        _autoDetected.Add(name);
    }

    private void Leave(string name)
    {
        _beforeLogin = [];
        _autoDetected.Remove(name);
        _partyXpKills.Remove(name);
    }

    private void GroupEnded()
    {
        _beforeLogin = [];
        _autoDetected.Clear();
        _partyXpKills.Clear();
    }

    private void CreditPartyKill(string name, string? primaryName)
    {
        if (_autoDetected.Contains(name) || _everNpc.Contains(name)
            || string.Equals(name, primaryName, StringComparison.OrdinalIgnoreCase))
            return;
        var kills = _partyXpKills.GetValueOrDefault(name) + 1;
        if (kills < PartyKillsToJoin) { _partyXpKills[name] = kills; return; }
        _partyXpKills.Remove(name);
        _autoDetected.Add(name);
        LastPartyKillPromotion = name;
        Version++;
    }

    /// <summary>The whitelist: the current members (<see cref="AutoDetected"/>, hand-added
    /// joins included), minus <paramref name="primaryName"/>, <paramref name="primaryPetName"/>
    /// (refuse a roster name equal to the user's OWN current pet — design survey §B
    /// "the user's own SK pet keeps its existing path"), and any name ever seen as an
    /// NPC target this session. A hand-added name is refused the same as an
    /// auto-detected one — the exclusion list protects the player from a typo or a
    /// stale name exactly as much as it protects the auto-detector.</summary>
    public IReadOnlyCollection<string> Roster(string? primaryName, string? primaryPetName)
    {
        var roster = new HashSet<string>(_autoDetected, StringComparer.OrdinalIgnoreCase);
        if (primaryName is { Length: > 0 }) roster.Remove(primaryName);
        if (primaryPetName is { Length: > 0 }) roster.Remove(primaryPetName);
        roster.ExceptWith(_everNpc);
        return roster;
    }

    /// <summary>Clears auto-detection state on a character switch / re-Select — a fresh
    /// primary character starts with no assumed group. Deliberately does NOT clear
    /// <see cref="_everNpc"/>; see its own doc.</summary>
    public void Reset()
    {
        _autoDetected.Clear();
        _partyXpKills.Clear();
        _partyXpLinesLeft = 0;
        _beforeLogin = [];
        _handAddedPassed = 0;
        LastLine = null;
        Version++;
    }

    /// <summary>Sets the hand-added joins for the watched character (<see cref="ManualTeammate"/>).
    /// Each joins the group just before the first line stamped at or after its moment —
    /// once — and is then a member like any other. Joins at or before
    /// <paramref name="pastThrough"/> are behind the reader already and are not placed
    /// again: a live edit leaves them to the re-derivation that follows it, which reads
    /// the log from the start and places every join in order.</summary>
    internal void ArmHandAdded(IEnumerable<(string Name, DateTime Since)> joins, DateTime? pastThrough)
    {
        _handAdded = [.. joins
            .Where(j => !string.IsNullOrWhiteSpace(j.Name))
            .Select(j => (Canonicalize(j.Name.Trim()), j.Since))
            .OrderBy(j => j.Item2)];
        _handAddedPassed = pastThrough is { } p ? _handAdded.Count(j => j.Since <= p) : 0;
        Version++;
    }

    /// <summary>A name the caller vouches for, in the group from this line on — the
    /// standalone feed's list (<see cref="DerivedTeammates.Observe"/>).</summary>
    internal void JoinByHand(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        if (_autoDetected.Add(Canonicalize(name.Trim()))) Version++;
    }

    // Places every armed hand-added join whose moment has come, before the line at ts.
    private void JoinHandAddedThrough(DateTime ts)
    {
        while (_handAddedPassed < _handAdded.Length && _handAdded[_handAddedPassed].Since <= ts)
        {
            var (name, since) = _handAdded[_handAddedPassed++];
            if (LastLine is null && since < ts) continue;   // older than the first line read: see LastLine
            if (_autoDetected.Add(name)) Version++;
        }
        LastLine = ts;
    }

    /// <summary>A copy that keeps every known-NPC exclusion and knows only
    /// <paramref name="members"/> — the members at the first line the watcher read (none
    /// after a Select; whoever was in the group when a "Reset session" split the log) — the
    /// starting point for re-deriving a session from that line, so membership is rebuilt
    /// in log order instead of whitelisting today's members retroactively.</summary>
    internal TeammateRoster WithoutMembers(IEnumerable<string>? members = null,
        IEnumerable<(string Name, DateTime Since)>? handAdded = null, DateTime? handAddedPastThrough = null)
    {
        var copy = new TeammateRoster();
        copy._everNpc.UnionWith(_everNpc);
        if (members is not null) copy._autoDetected.UnionWith(members);
        copy.ArmHandAdded(handAdded ?? [], handAddedPastThrough);
        copy.LastLine = handAddedPastThrough;
        return copy;
    }

    /// <summary>A hand-added name ("garg", "GARG") is canonicalised to the
    /// shape an EQ character name actually has (one capitalised word, e.g. "Garg")
    /// before it enters the roster set. Without this, <see cref="TeammatePerspective"/>'s
    /// exact-word matcher — deliberately <c>Ordinal</c>, so "Garg" can never match
    /// "Gargoyle" — never matches the log's own "Garg" against a manually-typed
    /// "garg", and every line naming that teammate silently produces no rewrite.
    /// Auto-detected names never need this: they are copied verbatim from a line the
    /// log itself already printed with the correct casing, and (the roster set being
    /// <c>OrdinalIgnoreCase</c>) an auto-detected entry already present is never
    /// overwritten by a differently-cased manual one for the same name.</summary>
    private static string Canonicalize(string name) =>
        name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant();
}
