using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EQBuddy.Core;

/// <summary>
/// Windows file-identity helpers for <c>TeammateLogPicker</c>'s and
/// <see cref="LogWatcher"/>'s same-file / inside-folder checks (repair round R4). A
/// normalized-path STRING compare cannot see that two different spellings — a hard
/// link, a junction, a symlink, an 8.3 short alias, or a mapped drive letter versus its
/// UNC path — name the SAME underlying file, so a player (deliberately or not) working
/// around one guard by handing EQBuddy a differently-spelled path to the SAME log
/// defeats it silently.
///
/// Identity is read from an OPENED HANDLE (<c>GetFileInformationByHandle</c>'s volume
/// serial number plus file index — stable across every alias above, because Windows
/// resolves reparse points and drive mappings before the handle is even returned) and
/// containment from the FINAL HANDLE PATH (<c>GetFinalPathNameByHandle</c>, which
/// reports the real, fully-resolved path a handle refers to). Both are BEST-EFFORT and
/// MUST NEVER throw or block the caller: opening a handle can fail for reasons that
/// have nothing to do with identity — the file is missing, exclusively locked, or
/// access is denied — and every public method here falls back to today's normalized,
/// case-insensitive path-STRING comparison whenever a handle cannot be opened. Not
/// Windows: same fallback (this app is Windows-only, but the class stays honest about
/// what it can promise elsewhere rather than assuming).
/// </summary>
public static class FileIdentity
{
    /// <summary>True when both paths name the same underlying file (or resolve to the
    /// same folder, for <see cref="IsInside"/>'s own use) — real identity when both
    /// handles open, else a normalized lexical fallback.</summary>
    public static bool SamePath(string a, string b)
    {
        var idA = TryGetId(a);
        var idB = TryGetId(b);
        if (idA is { } ia && idB is { } ib) return ia.Equals(ib);
        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when <paramref name="filePath"/> sits directly in
    /// <paramref name="folder"/> or in any subdirectory of it. Resolves both sides to
    /// their real, final path when a handle can be opened (so a junction or a symlink
    /// pointing INTO the folder is caught even though the two path strings share no
    /// prefix at all), falling back to a normalized directory-prefix compare
    /// otherwise.</summary>
    public static bool IsInside(string filePath, string folder)
    {
        // A MISSING file resolves no handle of its own, and the old fallback used the
        // LEXICAL directory of filePath — so a junction INTO folder was never followed
        // for a teammate path not yet synced down (repair round R7 / plan Part 5b-iii).
        // The PARENT directory exists even when the file inside it does not, so resolve
        // that instead: a real file still prefers its own final path (a file that is
        // itself a reparse point, however unlikely for a log, should resolve on its own
        // terms), a missing file falls back to its resolved parent, and only when
        // neither resolves does this fall all the way back to a lexical directory name.
        var fileDir = TryGetFinalPath(filePath) is { } realFile
            ? Path.GetDirectoryName(realFile) ?? ""
            : TryGetFinalPath(Path.GetDirectoryName(filePath) ?? "") is { } realDir
                ? realDir
                : Path.GetDirectoryName(Normalize(filePath)) ?? "";
        var root = (TryGetFinalPath(folder) ?? Normalize(folder))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fileDir.Equals(root, StringComparison.OrdinalIgnoreCase)
            || fileDir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }   // an unparsable path still deserves SOME answer, not a throw
    }

    private readonly record struct FileId(uint VolumeSerial, uint IndexHigh, uint IndexLow);

    /// <summary>One probe's outcome: both the identity AND the final path come off ONE
    /// opened handle rather than two separate opens. Only a TIMED-OUT probe is
    /// memoised (plan Part 5b-ii's "deny-list" — a path that is genuinely SLOW is
    /// answered lexically for the rest of <see cref="MemoTtl"/> rather than re-probed
    /// every call). A probe that returns FAST — success, missing file, access denied —
    /// is never cached: those cost nothing to repeat, and caching them would mean a
    /// file that appears (or a permission that changes) between two calls keeps
    /// answering its OLD result until the TTL expires — exactly the failure mode a
    /// continuous revalidation caller (LogWatcher's Poll, Part 5b-iv) exists to
    /// close.</summary>
    private readonly record struct Probe(FileId? Id, string? FinalPath, bool TimedOut, DateTime Stamp);

    private static readonly TimeSpan MemoTtl = TimeSpan.FromSeconds(30);
    private static readonly Dictionary<string, Probe> _memo = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _memoLock = new();
    private static int _loggedTimeoutOnce;

    private static FileId? TryGetId(string path) => GetProbe(path).Id;
    private static string? TryGetFinalPath(string path) => GetProbe(path).FinalPath;

    private static Probe GetProbe(string path)
    {
        lock (_memoLock)
            if (_memo.TryGetValue(path, out var cached) && DateTime.UtcNow - cached.Stamp < MemoTtl)
                return cached;

        var probe = RunProbe(path);
        lock (_memoLock)
        {
            if (probe.TimedOut) _memo[path] = probe;
            else _memo.Remove(path);   // a fresh fast answer supersedes any stale timeout memo
        }
        return probe;
    }

    private static Probe RunProbe(string path)
    {
        if (!OperatingSystem.IsWindows()) return new Probe(null, null, false, DateTime.UtcNow);
        var (handle, timedOut) = TryOpenBounded(path);
        using var h = handle;
        if (h is null) return new Probe(null, null, timedOut, DateTime.UtcNow);
        try
        {
            var id = GetFileInformationByHandle(h, out var info)
                ? new FileId(info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow)
                : (FileId?)null;
            var finalPath = ReadFinalPath(h);
            return new Probe(id, finalPath, false, DateTime.UtcNow);
        }
        catch { return new Probe(null, null, false, DateTime.UtcNow); }
    }

    private static string? ReadFinalPath(SafeFileHandle handle)
    {
        try
        {
            var buf = new char[1024];
            var len = GetFinalPathNameByHandleW(handle, buf, (uint)buf.Length, 0);
            if (len == 0 || len >= buf.Length) return null;
            var result = new string(buf, 0, (int)len);
            // Strip the \\?\ (or \\?\UNC\) extended-length prefix GetFinalPathNameByHandle
            // always returns — everything downstream compares against ordinary paths.
            return result.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)
                ? @"\\" + result[8..]
                : result.StartsWith(@"\\?\", StringComparison.Ordinal) ? result[4..] : result;
        }
        catch { return null; }
    }

    /// <summary>An unavailable SMB/UNC path can leave <c>CreateFileW</c> blocked for
    /// tens of seconds with no timeout parameter of its own (plan Part 5b-ii) — this is
    /// what finally makes the class's "never blocks" contract true. The call cannot be
    /// CANCELLED, only bounded: run it on the thread pool and wait at most
    /// <see cref="ProbeTimeout"/>; on timeout, hand the orphaned task its own disposal
    /// (it closes the handle itself once — if ever — it returns) and answer null, which
    /// callers already treat as "fall back to a lexical comparison".</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(750);

    private static (SafeFileHandle? Handle, bool TimedOut) TryOpenBounded(string path)
    {
        var task = Task.Run(() => TryOpen(path));
        if (task.Wait(ProbeTimeout)) return (task.Result, false);
        if (Interlocked.Exchange(ref _loggedTimeoutOnce, 1) == 0)
            CoreLog.Error(new TimeoutException(
                $"FileIdentity probe exceeded {ProbeTimeout.TotalMilliseconds}ms; falling back to lexical comparison."));
        _ = task.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result?.Dispose(); },
            TaskScheduler.Default);
        return (null, true);
    }

    private static SafeFileHandle? TryOpen(string path)
    {
        try
        {
            var isDir = Directory.Exists(path);
            var flags = FILE_FLAG_BACKUP_SEMANTICS_IF_DIR(isDir);
            var handle = CreateFileW(path, GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero, OPEN_EXISTING, flags, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); return null; }
            return handle;
        }
        catch { return null; }
    }

    private static uint FILE_FLAG_BACKUP_SEMANTICS_IF_DIR(bool isDir) => isDir ? 0x02000000u : 0u;

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x1, FILE_SHARE_WRITE = 0x2, FILE_SHARE_DELETE = 0x4;
    private const uint OPEN_EXISTING = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle hFile, [Out] char[] lpszFilePath, uint cchFilePath, uint dwFlags);
}
