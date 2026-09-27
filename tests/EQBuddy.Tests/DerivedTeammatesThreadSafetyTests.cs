using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>
/// Audit finding (major): <see cref="DerivedTeammates"/> is fed on the poll thread
/// (<see cref="DerivedTeammates.Observe"/>) while the UI thread reads it
/// (<see cref="DerivedTeammates.KnownTeammates"/>/<see cref="DerivedTeammates.Snapshots"/>/
/// <see cref="DerivedTeammates.AutoDetected"/>) with no lock guarding either side's plain
/// <c>Dictionary</c>/<c>HashSet</c>. A UI-thread snapshot taken mid-poll can throw
/// "Collection was modified" or read a torn dictionary. There is no single injectable
/// hook to force the exact interleaving deterministically (unlike <c>LogWatcher</c>'s
/// gated I/O), so this is a stress reproduction: two threads hammer the same instance —
/// one growing the known-teammate set and ticking it every line, the other reading it —
/// for long enough that an unguarded race reliably surfaces. Prove-failed against the
/// unlocked implementation before the fix (reliably threw within well under a second);
/// the locked version below runs the same duration and throws never, because a poll and
/// a read can no longer interleave inside either collection at all.
/// </summary>
public class DerivedTeammatesThreadSafetyTests
{
    private const string Primary = "Smargush";

    [Fact]
    public void ConcurrentObserveAndReadsNeverThrow()
    {
        var derived = new DerivedTeammates();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);

        using var cts = new CancellationTokenSource();
        Exception? caught = null;

        var writers = Enumerable.Range(0, 8).Select(w => Task.Run(() =>
        {
            var i = 0;
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var ts = t0.AddSeconds(i);
                    // A brand-new roster name every single call — every writer
                    // iteration is an Add into _stats (never an update to an existing
                    // key), which is what forces repeated growth/resize of the
                    // dictionary a concurrent enumerator is reading.
                    var name = $"Mate{w}_{i}";
                    derived.Observe(ts, $"{name} slashes a rat for 1 points of damage.", Primary, null, [name]);
                    i++;
                }
            }
            catch (Exception ex) { caught ??= ex; cts.Cancel(); }
        })).ToArray();

        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    _ = derived.KnownTeammates.Count;
                    _ = derived.AutoDetected.Count;
                    _ = derived.Snapshots().Count;
                    if (derived.Version < 0) throw new InvalidOperationException("version went negative");
                }
            }
            catch (Exception ex) { caught ??= ex; cts.Cancel(); }
        })).ToArray();

        var all = writers.Concat(readers).ToArray();
        Thread.Sleep(TimeSpan.FromSeconds(5));
        cts.Cancel();
        Assert.True(Task.WaitAll(all, TimeSpan.FromSeconds(15)),
            "a writer/reader task did not finish — deadlock?");

        Assert.Null(caught);
    }
}
