using System.Text.RegularExpressions;

namespace EQBuddy.Core;

/// <summary>
/// Auto-detects group membership from the PRIMARY player's own log, so a duo/group
/// doesn't have to hand-type every teammate's name into settings. Pure incremental
/// state, fed one raw (already-normalized) message at a time — no file I/O, no
/// durable store, nothing persisted beyond the in-memory session this instance lives
/// for. Merged with the user's own manually-typed list (<c>AppSettings.TeammateNames</c>)
/// by <see cref="Roster"/>, which is the ONE place both sources combine.
///
/// <b>Signals used (from the design survey), and one deliberately NOT implemented:</b>
/// join/leave/invite lines, "X tells the group", and "Targeted (NPC): X" (an exclusion,
/// never an inclusion) are exact, deterministic string shapes — cheap to get right and
/// cheap to test. The survey's fourth signal, kill-plus-party-XP timing correlation, is
/// a probabilistic join over two independent line streams within a time window; it is
/// NOT implemented here — the manual list in Options is the fallback for a teammate this
/// class never sees announced (someone already in the group when you logged in, in a
/// log whose window predates any join/invite/tell line). Documented rather than faked.
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

    private readonly HashSet<string> _autoDetected = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every name this session has ever seen the client label an NPC — a
    /// whitelist member is refused permanently once its name shows up here, since the
    /// same string can never be trusted as a player again this session (design survey
    /// §F: "the risk comes from generic single-word names"). Never cleared by
    /// <see cref="Reset"/> — an NPC identity does not become a player identity when the
    /// session rolls, so the exclusion should outlive it; the cost of a name staying
    /// excluded across a reset is a manual re-add, the cost of losing the exclusion is
    /// counting a mob as a teammate.</summary>
    private readonly HashSet<string> _everNpc = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Names auto-detected so far this session (group joins/invites/tells).
    /// Exposed read-only so Options → Behavior can show "detected: Garg, Yungweezy" —
    /// never itself the roster; see <see cref="Roster"/> for exclusions.</summary>
    public IReadOnlyCollection<string> AutoDetected => _autoDetected;

    /// <summary>Moves whenever the auto-detected set or the known-NPC set changes, so a
    /// caller can cache <see cref="Roster"/> between the rare lines that move it.</summary>
    internal int Version { get; private set; }

    /// <summary>Feed one already-split (timestamp stripped) log message. Cheap early-out:
    /// every pattern here anchors at the start of the line, so a single ordinal
    /// starts-with/contains check per pattern is enough before the regex runs.
    ///
    /// Membership tracks who is in the group NOW, not everyone who ever appeared to
    /// be: a name is added on a real join (an accepted invite or the join line) and
    /// removed on that person leaving, being removed, or the group disbanding — see
    /// each regex's own doc for the line it answers. A name dropped here does not
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
    public void Observe(string msg, string? primaryName)
    {
        if (string.IsNullOrEmpty(msg)) return;
        // Every shape below names the group or the party, or is a Targeted line — one
        // vectorised scan skips the regexes on the other ~99.9% of a long replay.
        if (!msg.Contains("group", StringComparison.Ordinal) && !msg.Contains(" party.", StringComparison.Ordinal)
            && !msg.StartsWith("Targeted (NPC): ", StringComparison.Ordinal))
            return;

        Match m;
        if ((m = JoinedGroupRx.Match(msg)).Success) _autoDetected.Add(m.Groups["name"].Value);
        else if ((m = AgreedToJoinRx.Match(msg)).Success) _autoDetected.Add(m.Groups["name"].Value);
        else if ((m = TellsGroupRx.Match(msg)).Success) _autoDetected.Add(m.Groups["name"].Value);
        else if ((m = LeftGroupRx.Match(msg)).Success) _autoDetected.Remove(m.Groups["name"].Value);
        else if ((m = RemovedFromPartyRx.Match(msg)).Success)
        {
            var removed = m.Groups["name"].Value;
            if (primaryName is { Length: > 0 } && string.Equals(removed, primaryName, StringComparison.Ordinal))
                _autoDetected.Clear();
            else
                _autoDetected.Remove(removed);
        }
        else if (UserRemovedRx.IsMatch(msg) || UserLeftOrDisbandedRx.IsMatch(msg)) _autoDetected.Clear();
        else if (msg[0] == 'T' && (m = TargetedNpcRx.Match(msg)).Success) _everNpc.Add(m.Groups["name"].Value);
        else return;
        // A matched line may still change nothing (a repeated join), but a membership
        // swap of equal size would not move either count — so any matched line bumps.
        Version++;
    }

    /// <summary>The whitelist: auto-detected names, unioned with the caller's manual
    /// list, minus <paramref name="primaryName"/>, <paramref name="primaryPetName"/>
    /// (refuse a roster name equal to the user's OWN current pet — design survey §B
    /// "the user's own SK pet keeps its existing path"), and any name ever seen as an
    /// NPC target this session. A manually-typed name is refused the same as an
    /// auto-detected one — the exclusion list protects the player from a typo or a
    /// stale name exactly as much as it protects the auto-detector.</summary>
    public IReadOnlyCollection<string> Roster(string? primaryName, string? primaryPetName,
        IReadOnlyCollection<string>? manualNames)
    {
        var roster = new HashSet<string>(_autoDetected, StringComparer.OrdinalIgnoreCase);
        if (manualNames is not null)
            foreach (var n in manualNames)
                if (!string.IsNullOrWhiteSpace(n)) roster.Add(Canonicalize(n.Trim()));

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
        Version++;
    }

    /// <summary>A hand-typed roster entry ("garg", "GARG") is canonicalised to the
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
