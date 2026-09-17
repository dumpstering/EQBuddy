namespace EQBuddy.Core;

/// <summary>
/// Repair round C8: the primary's log and the teammate's log are written by two
/// different computers with two different clocks, and every timestamp either one
/// parses is an unqualified LOCAL <see cref="DateTime"/> — <c>LogParser.TryParseTimestamp</c>
/// carries no timezone and no drift correction (LogParser.cs is off-limits for this
/// repair round; this class exists so the correction can live somewhere that isn't).
/// A timezone difference between the two machines, or ordinary clock skew, shifts the
/// WHOLE of one side's timeline against the other: two sequential fights can look
/// overlapping (undercounting combat time through <c>DuoCompanion.UnionCombatSeconds</c>),
/// two concurrent fights can look disjoint, timeline buckets shift, and stale teammate
/// history can slip past <see cref="TeammateLogTail.PrimarySessionStart"/>'s gate.
///
/// <b>What this deliberately does NOT do: normalise either log's timestamps.</b> That
/// is a real project of its own — every consumer of a teammate timestamp (the combat
/// union, the timeline buckets, the session-start gate, the original raw timestamp
/// merge <see cref="TeammateLogTail.DrainBefore"/> already does) would need to agree
/// on which side's clock to trust and by how much, and there is no way to know that
/// from the logs alone — a bystander-visible kill only proves the two events were
/// close in real time, not which side's stamp is more correct. Instead: <b>estimate</b>
/// the offset from events BOTH logs independently record, <b>surface</b> the estimate
/// honestly (including "cannot be determined" when no such event has been seen yet),
/// and <b>refuse to combine</b> when the estimate is too large to trust — see
/// <see cref="Estimate"/>, <see cref="ExceedsThreshold"/> and <see cref="Threshold"/>.
///
/// The events used are bystander-visible kills: the primary's own log sees a
/// third-party kill line ("An orc pawn has been slain by Buddy!") the moment the
/// teammate lands it, stamped by the PRIMARY's clock; the teammate's own log
/// self-reports the identical kill ("You have slain an orc pawn!"), stamped by the
/// TEAMMATE's clock. <see cref="DuoCompanion"/> (see <c>DuoCompanion.cs</c>, repair
/// round C3's promoted-kill machinery) is what joins the two by target name and time
/// of arrival; this class only turns a stream of (primary time, teammate time) pairs
/// for the SAME real-world moment into a single robust estimate.
/// </summary>
public sealed class ClockDriftEstimator
{
    /// <summary>Chosen and documented here, deliberately, rather than derived: two
    /// NTP-synced consumer clocks drift by a few seconds a DAY, not minutes, so an
    /// estimate beyond two minutes is far more likely to mean "wrong timezone",
    /// "one machine's clock is simply wrong", or "this pair of kills wasn't really
    /// the same event" than ordinary skew — any of which makes combining the two
    /// timelines actively misleading rather than merely imprecise. Two minutes is
    /// also generous enough that the false-refusal cost (a genuinely well-synced
    /// pair occasionally measured a little high from one noisy sample) stays rare;
    /// see <see cref="Estimate"/> for how a single bad sample is kept from swinging
    /// the estimate on its own.</summary>
    public static readonly TimeSpan Threshold = TimeSpan.FromMinutes(2);

    /// <summary>How many recent samples the estimate is built from. Small and
    /// deliberately bounded: clock skew can change mid-session (a machine's clock
    /// gets corrected, or the estimate was simply wrong from a mismatched kill), so
    /// an old sample should eventually age out rather than being weighed forever
    /// alongside fresh ones.</summary>
    private const int MaxSamples = 8;

    private readonly Queue<TimeSpan> _samples = new();
    private readonly object _lock = new();
    private const int MaxPendingKills = 256;
    private readonly List<(string Target, DateTime Time)> _primaryKills = [];
    private readonly List<(string Target, DateTime Time)> _teammateKills = [];

    // Each occurrence is consumed once. Either log may arrive first (or have an
    // ahead clock); never reuse the previous kill of a respawning creature.
    internal void ObservePrimaryKill(string target, DateTime time) => ObserveKill(target, time, true);
    internal void ObserveTeammateKill(string target, DateTime time) => ObserveKill(target, time, false);

    private void ObserveKill(string target, DateTime time, bool primary)
    {
        lock (_lock)
        {
            var own = primary ? _primaryKills : _teammateKills;
            var other = primary ? _teammateKills : _primaryKills;
            var match = other.FindIndex(k => k.Target.Equals(target, StringComparison.OrdinalIgnoreCase));
            if (match >= 0)
            {
                var matched = other[match];
                other.RemoveAt(match);
                RecordSample(primary ? time : matched.Time, primary ? matched.Time : time);
            }
            else
            {
                own.Add((target, time));
                if (own.Count > MaxPendingKills) own.RemoveAt(0);
            }
        }
    }

    internal void ClearPendingKills()
    {
        lock (_lock) { _primaryKills.Clear(); _teammateKills.Clear(); }
    }

    /// <summary>Records one offset sample: <paramref name="primaryTimestamp"/> is
    /// when the PRIMARY's own log recorded a bystander-visible kill attributable to
    /// the teammate; <paramref name="teammateTimestamp"/> is when the TEAMMATE's own
    /// log self-reported that same kill. Positive means the primary's clock reads
    /// AHEAD of the teammate's.</summary>
    public void RecordSample(DateTime primaryTimestamp, DateTime teammateTimestamp)
    {
        lock (_lock)
        {
            _samples.Enqueue(primaryTimestamp - teammateTimestamp);
            while (_samples.Count > MaxSamples) _samples.Dequeue();
        }
    }

    /// <summary>The current best estimate of the offset — the MEDIAN of recent
    /// samples, not the mean: a single mismatched pair (two same-named targets
    /// killed close together, joined to the wrong one) moves a median by at most one
    /// rank, where it can drag a mean arbitrarily far. Null when no sample has ever
    /// been recorded, reported as "cannot be determined" rather than defaulting to
    /// zero — a silent zero would claim synchronised clocks nothing has actually
    /// confirmed.</summary>
    public TimeSpan? Estimate
    {
        get
        {
            lock (_lock)
            {
                if (_samples.Count == 0) return null;
                var sorted = _samples.Order().ToArray();
                return sorted[sorted.Length / 2];
            }
        }
    }

    /// <summary>How many samples the current <see cref="Estimate"/> is built from —
    /// 0 exactly when <see cref="Estimate"/> is null.</summary>
    public int SampleCount { get { lock (_lock) return _samples.Count; } }

    /// <summary>True only once an estimate actually exists AND its magnitude exceeds
    /// <see cref="Threshold"/> — "cannot be determined yet" (no samples) is never
    /// treated as "exceeds", matching the instruction to report the undetermined
    /// case honestly rather than refuse to combine by default before anything has
    /// even been observed.</summary>
    public bool ExceedsThreshold
    {
        get
        {
            var e = Estimate;
            return e is { } offset && offset.Duration() > Threshold;
        }
    }

    /// <summary>Drops every recorded sample — a new teammate pairing (a different
    /// log, potentially a different machine entirely) starts the estimate over
    /// rather than carrying a stranger's clock offset forward.</summary>
    internal void Reset()
    {
        lock (_lock) { _samples.Clear(); ClearPendingKills(); }
    }
}
