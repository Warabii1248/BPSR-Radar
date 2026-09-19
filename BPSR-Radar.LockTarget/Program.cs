using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

// BPSR-Radar.LockTarget: read-only external memory reader that reports the
// current manual lock-on target UUID inside the game process.
//
// Pure observation: OpenProcess with PROCESS_VM_READ | PROCESS_QUERY_INFORMATION
// only. No writes, no injection, no hooks, no client modification.
// Must run elevated: the game runs at a higher integrity level, so a
// non-elevated caller gets OpenProcess error 5.
//
// The target lives in a single record whose layout is documented in
// LockRecord.cs, found on 2026-09-19 by having the player mark each lock by
// hand and diffing memory at those instants. It carries the uuid and, at
// +0x30, whether the lock is the manual one or the automatic provisional one
// the game takes on whatever comes into attack range. Only the manual lock is
// published.
//
// Everything before that chased a list container that turned out to be the
// scene's entity registry -- one node per entity, holding zones and bullets
// and the player alike. Scanner.cs still implements that search because the
// offline harness modes analyse recordings made with it.
//
// Modes:
//   <pid> [statefile] [--snapshot-dir <dir>] [--max-snapshots N]
//       Normal watch. With --snapshot-dir, also records ground-truth sweeps
//       when the player marks a lock change, and memory snapshots at moments
//       worth re-examining offline.
//   --replay <dir>            re-run the old element scan over snapshots
//   --test-signature <dir>    run the lock-record signature over snapshots
//   --diff-marks <dir>        find the target field and the lock-kind flag
//   --dump-elements <dir>     lay candidate elements out side by side
//   --find-target-field <dir> addresses whose monster changes between snapshots
//   --inspect-addr <dir> <a>  memory around an address in every snapshot
//   --selftest                snapshot round-trip, scanner, record hold, marks

static class Native
{
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;
    public const uint PROCESS_VM_READ = 0x0010;
    public const uint MEM_COMMIT = 0x1000;
    public const uint MEM_PRIVATE = 0x20000;
    public const uint PAGE_GUARD = 0x100;
    public const uint PAGE_NOACCESS = 0x01;
    public const uint PAGE_READWRITE = 0x04;
    public const uint PAGE_READONLY = 0x02;
    public const uint PAGE_WRITECOPY = 0x08;
    public const uint PAGE_EXECUTE_READ = 0x20;
    public const uint PAGE_EXECUTE_READWRITE = 0x40;
    public const uint PAGE_EXECUTE_WRITECOPY = 0x80;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr h);

    // Reads one key's state. Not a hook: nothing is installed, no other key
    // is observed, and the game is not touched.
    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint VirtualQueryEx(IntPtr hProcess, IntPtr addr, out MEMORY_BASIC_INFORMATION64 mbi, nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr baseAddr, byte[] buffer, nint size, out nint read);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION64
    {
        public ulong BaseAddress;
        public ulong AllocationBase;
        public uint AllocationProtect;
        public uint __alignment1;
        public ulong RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint __alignment2;
    }
}

static class Program
{
    const int PollMs = 100;
    const int HeartbeatMs = 2000;
    // Floor between scans however urgent the trigger, so a held key or a
    // list filling one entity at a time cannot turn into a scan loop.
    const int MinScanGapMs = 400;

    // Backoff between scans while the record's address is still unknown. It
    // starts short, because the usual case is that the record is about to
    // become findable, and doubles on every failure up to MaxRescanMs.
    //
    // The growth is the point. A player standing among monsters without
    // locking anything is a state that can last for minutes, and a fixed
    // short backoff turns it into a two second all-core scan every three
    // seconds -- which is the defect this whole line of work set out to
    // remove. The urgent triggers below ignore the backoff, so the cases
    // where a scan is actually likely to succeed stay fast however far it
    // has grown.
    const int RescanMs = 750;
    const int MaxRescanMs = 15000;

    // The key or mouse button the player locks with, as a virtual-key code.
    // Zero disables the trigger. The game's default is the middle mouse
    // button, but the binding varies and a controller has none at all, which
    // is why this is one of three ways a scan gets started rather than the
    // only one.
    static int LockKeyVk;
    static bool lockKeyWasDown;

    // True on the press, not while held.
    static bool LockKeyPressed()
    {
        if (LockKeyVk == 0) return false;
        bool down = (Native.GetAsyncKeyState(LockKeyVk) & 0x8000) != 0;
        bool edge = down && !lockKeyWasDown;
        lockKeyWasDown = down;
        return edge;
    }
    // How long a watched address may go without ever holding a lock before it
    // is treated as stale. Generous: a player can stand around for minutes.
    const int StaleRecordMs = 10 * 60 * 1000;
    // Consecutive bad reads before an element is dropped. Lock transitions
    // briefly leave slots half-written; a single bad read must not trigger
    // a rescan.
    const int ElemMaxBadReads = 5;
    const int MaxWatchElems = 128;

    static string StatePath = Path.Combine(Path.GetTempPath(), "BPSR-Radar", "locktarget.json");
    static string WorkDir => Path.GetDirectoryName(StatePath)!;
    static string EventLogPath => Path.Combine(WorkDir, "lock-events.log");
    static string EntitiesPath => Path.Combine(WorkDir, "entities.json");
    static string MarksPath => Path.Combine(WorkDir, "marks.jsonl");
    static int lastMarkSeq;

    // The helper outlives the app on purpose (it exits with the game), which
    // means a stale one keeps running after the app is restarted with
    // different settings, and the app will not start a second one. It cannot
    // be stopped from outside either, being elevated. So it retires itself:
    // the app writes this file naming the pid it wants gone.
    static string StopRequestPath => Path.Combine(WorkDir, "helper-stop");

    // Identifies what this helper was started for. The app compares it with
    // what it wants and asks for a restart when they differ.
    static string ConfigStamp = "";

    // The app names the helper it wants gone by that helper's own process id
    // (LockTargetService.RequestHelperStop writes p.Id). This compared it
    // against curPid, which is the *game's* pid -- so the match never
    // happened and the mechanism had never once worked. What looked like
    // "a helper predating the mechanism" on 2026-09-19 was this.
    //
    // Only the helper's own pid is accepted. Accepting the game's as well
    // would be worse than useless: the request file is left behind after a
    // helper retires, and every helper started afterwards would read a game
    // pid that still matches and retire immediately.
    internal static bool StopRequestMatches(string? fileContent, int selfPid)
    {
        if (string.IsNullOrWhiteSpace(fileContent)) return false;
        // A comma-separated list, because more than one helper can be alive
        // and every one of them has to be named. A single pid still parses.
        foreach (var part in fileContent.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part.Trim(), out int want) && want == selfPid) return true;
        }
        return false;
    }

    // Test seams. StopRequested is the call site that once passed the wrong
    // pid, so the selftest has to run the real thing; pointing StatePath at a
    // scratch directory keeps it away from a live app's pending request.
    internal static string StatePathForTests
    {
        get => StatePath;
        set => StatePath = value;
    }

    internal static bool StopRequestedForTests() => StopRequested();

    static bool StopRequested()
    {
        try
        {
            if (!File.Exists(StopRequestPath)) return false;
            string want = File.ReadAllText(StopRequestPath);
            if (!StopRequestMatches(want, Environment.ProcessId)) return false;
            // Drop this pid and leave the rest. Deleting the file outright
            // would cancel the request for every other helper named in it.
            var rest = want.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0 && x != Environment.ProcessId.ToString())
                .ToArray();
            try
            {
                if (rest.Length == 0) File.Delete(StopRequestPath);
                else File.WriteAllText(StopRequestPath, string.Join(",", rest));
            }
            catch { }
            return true;
        }
        catch { return false; }
    }

    static readonly object logLock = new();

    // The event log is the flight recorder a bug report is built from, so it
    // stays on in a released build -- but it is appended to on every scan and
    // every lock change and nothing ever removed it. Rolled at a megabyte,
    // keeping one previous file.
    const long MaxLogBytes = 1024 * 1024;

    static void RollLogIfLarge(string path)
    {
        try
        {
            var f = new FileInfo(path);
            if (!f.Exists || f.Length < MaxLogBytes) return;
            string prev = path + ".1";
            if (File.Exists(prev)) File.Delete(prev);
            File.Move(path, prev);
        }
        catch { }
    }

    // Timestamped flight-recorder for offline analysis: every scan and uuid
    // transition lands here so detection latency can be verified after a
    // play session without watching.
    static void LogEvent(string msg)
    {
        try
        {
            lock (logLock)
            {
                RollLogIfLarge(EventLogPath);
                File.AppendAllText(EventLogPath, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n");
            }
        }
        catch { }
    }

    static KnownEntities Known = null!;

    // Snapshot capture settings (recording sessions only).
    static string? SnapshotDir;
    static int MaxSnapshots = 8;
    static int snapshotsTaken;
    static DateTime lastSnapshotAt = DateTime.MinValue;
    static readonly TimeSpan SnapshotMinGap = TimeSpan.FromSeconds(30);

    // Last-written state, replayed by a background timer so the reader never
    // sees a stale file while a long scan or rescan wait is in progress.
    static readonly object stateLock = new();
    static int curPid;
    static long curUuid;
    static string curState = "";
    static string? curDetail;

    // Discriminators learned from confirmed lock-list elements.
    static readonly HashSet<ulong> learnedOwners = new();
    static readonly HashSet<ulong> learnedVtbls = new();
    // Memory regions that produced hits on the last successful scan; rescans
    // are restricted to these instead of the whole address space.
    static List<(ulong b, ulong s)> learnedRegions = new();
    // Elements currently being polled -> consecutive bad reads.
    static readonly Dictionary<ulong, int> watchElems = new();
    static int fullScanCounter;

    static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--selftest")
        {
            int rc = SelfTest();
            if (rc != 0) return rc;
            rc = SelfTestScan.Run();
            if (rc != 0) return rc;
            rc = SelfTestHold.Run();
            if (rc != 0) return rc;
            return SelfTestMarks.Run();
        }
        if (args.Length >= 1 && args[0] == "--selftest-hold")
        {
            return SelfTestHold.Run();
        }
        if (args.Length >= 1 && args[0] == "--selftest-marks")
        {
            return SelfTestMarks.Run();
        }
        if (args.Length >= 1 && args[0] == "--selftest-scan")
        {
            return SelfTestScan.Run();
        }
        if (args.Length >= 2 && args[0] == "--replay")
        {
            return Replay(args[1]);
        }
        if (args.Length >= 2 && args[0] == "--test-signature")
        {
            return TestSignature(args[1]);
        }
        if (args.Length >= 2 && args[0] == "--diff-marks")
        {
            return DiffMarks(args[1]);
        }
        if (args.Length >= 3 && args[0] == "--inspect-addr")
        {
            return InspectAddr(args[1], args[2..]);
        }
        if (args.Length >= 2 && args[0] == "--find-target-field")
        {
            return FindTargetField(args[1]);
        }
        if (args.Length >= 2 && args[0] == "--dump-elements")
        {
            return DumpElements(args[1]);
        }

        if (args.Length < 1 || !int.TryParse(args[0], out int pid))
        {
            Console.Error.WriteLine("usage: BPSR-Radar.LockTarget.exe <pid> [statefile] [--snapshot-dir <dir>] [--max-snapshots N]");
            Console.Error.WriteLine("       BPSR-Radar.LockTarget.exe --replay <dir>");
            Console.Error.WriteLine("       BPSR-Radar.LockTarget.exe --selftest");
            return 2;
        }

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--snapshot-dir" when i + 1 < args.Length:
                    SnapshotDir = args[++i];
                    break;
                case "--max-snapshots" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int n)) MaxSnapshots = n;
                    break;
                case "--lock-key" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int vk)) LockKeyVk = vk;
                    break;
                default:
                    if (!args[i].StartsWith("--")) StatePath = args[i];
                    break;
            }
        }

        curPid = pid;
        Known = new KnownEntities(EntitiesPath);
        // Build time makes a redeployed binary look different to the app even
        // when its arguments match, so an upgrade does not need the game shut.
        long build = 0;
        try { build = File.GetLastWriteTimeUtc(Environment.ProcessPath!).Ticks; } catch { }
        ConfigStamp = $"{SnapshotDir ?? ""}|{MaxSnapshots}|{build}|{LockKeyVk}";
        LogEvent($"start pid={pid}{(SnapshotDir != null ? $" snapshot-dir={SnapshotDir}" : "")}");

        using var heartbeat = new Timer(_ =>
        {
            lock (stateLock) WriteStateFile(curPid, curUuid, curState, curDetail);
        }, null, HeartbeatMs, HeartbeatMs);

        try
        {
            return Run(pid);
        }
        catch (Exception ex)
        {
            WriteState(pid, 0, "error: " + ex.Message);
            LogEvent($"error {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    static int Run(int pid)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero)
        {
            WriteState(pid, 0, $"error: OpenProcess failed {Marshal.GetLastWin32Error()}");
            return 1;
        }

        var mem = new LiveMem(h);
        var scratch = new byte[LockRecord.ReadSize];

        try
        {
            var records = new List<ulong>();
            // Addresses that held the record before. A field transition frees
            // and rebuilds the scene, which changes the owner pointer -- but
            // measured 2026-09-19, the record comes back at the same address
            // (0x2a64e3ec950 before and after). Re-checking those few
            // addresses costs one read each and skips the scan entirely.
            var sticky = new List<ulong>();
            var owners = new Dictionary<ulong, ulong>();
            long lastUuid = 0;
            bool sawFirstLock = false;
            var lastScan = Stopwatch.StartNew();
            bool scannedOnce = false;
            int monstersAtLastScan = int.MaxValue;
            int backoff = RescanMs;
            var lastBeat = Stopwatch.StartNew();
            var lastValid = Stopwatch.StartNew();

            for (;;)
            {
                if (ProcessExited(pid)) { LogEvent("game exited"); return 0; }
                if (StopRequested()) { LogEvent("stop requested by the app"); return 0; }
                Known.Refresh();

                // Ground-truth sweeps: the player marks the instant they lock
                // or clear a target, and memory is sampled right then.
                if (SnapshotDir != null)
                {
                    foreach (var mark in MarkScan.ReadNew(MarksPath, lastMarkSeq))
                    {
                        lastMarkSeq = Math.Max(lastMarkSeq, mark.N);
                        MarkScan.Sweep(mem, Known, Path.Combine(SnapshotDir, "marks"), mark, LogEvent);
                    }
                }

                // A scan is only ever needed to learn the address. The record
                // stays put for the life of the game process, so once it is
                // known the loop is a 0x40 byte read every PollMs and memory
                // is never walked again.
                //
                // Measured before this held: 90 scans in seven minutes, 2.1 s
                // each, 57 of them finding nothing -- 188 seconds of all-core
                // scanning underneath the game, a 45% duty cycle.
                var gate = CheckGate(records.Count > 0, Known);
                if (gate != Gate.Proceed)
                {
                    if (gate == Gate.NoMonsters && lastUuid != 0)
                    {
                        LogEvent($"uuid 0x{lastUuid:x} -> 0x0 tms={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
                        lastUuid = 0;
                    }
                    WriteState(pid, 0, gate == Gate.NoMonsters ? "no_monsters" : "waiting_for_entities",
                        $"known={Known.Count}/mon={Known.MonsterCount}");
                    if (!WaitForRescan(pid, 2)) return 0;
                    continue;
                }

                // Try the addresses that held it last before walking memory.
                //
                // The scan can only find the record while a lock is held: it
                // matches on the uuid in the slot. So after a transition it
                // fails unless the player happens to be holding a lock during
                // the two seconds it runs, and the wait becomes scan, back
                // off, scan again -- measured as two failed scans and about
                // five seconds before the first target appeared. Checking the
                // old addresses instead catches it on the next poll.
                if (records.Count == 0 && sticky.Count > 0)
                {
                    var again = Readopt(mem, sticky, Known, scratch, owners);
                    if (again.Count > 0)
                    {
                        records = again;
                        lastValid.Restart();
                        LogEvent($"record 0x{again[0]:x} back without a scan (of {sticky.Count} remembered)");
                    }
                }

                // The backoff applies between scans, not before the first one.
                long sinceScan = scannedOnce ? lastScan.ElapsedMilliseconds : long.MaxValue;
                bool pressed = LockKeyPressed();
                if (ShouldScan(records.Count > 0, sinceScan, Known.MonsterCount, Known.Fresh,
                        monstersAtLastScan, pressed, backoff))
                {
                    var sw = Stopwatch.StartNew();
                    owners.Clear();
                    var found = LockRecord.Find(mem, Known, out string diag, owners: owners);
                    sw.Stop();
                    lastScan.Restart();
                    scannedOnce = true;
                    monstersAtLastScan = Known.MonsterCount;
                    records = found;
                    if (found.Count > 0) sticky = new List<ulong>(found);

                    if (records.Count == 0)
                    {
                        backoff = Math.Min(backoff * 2, MaxRescanMs);
                        LogEvent($"scan ms={sw.ElapsedMilliseconds} {diag} known={Known.Count}/mon={Known.MonsterCount}" +
                                 (pressed ? " (lock key)" : "") + $" next={backoff}ms");
                        WriteState(pid, 0, "waiting_for_lock", diag);
                        if (lastUuid != 0)
                        {
                            LogEvent($"uuid 0x{lastUuid:x} -> 0x0 tms={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
                            lastUuid = 0;
                        }
                        if (!WaitForRescan(pid, 4)) return 0;
                        continue;
                    }
                    backoff = RescanMs;
                    lastValid.Restart();
                    LogEvent($"scan ms={sw.ElapsedMilliseconds} {diag} records={records.Count}");
                }

                // Poll. Several copies of the record can exist and they agree;
                // a manual lock always wins, because the automatic one follows
                // whatever wanders into attack range and must never drive the
                // display.
                long manual = 0;
                long auto = 0;
                ulong at = 0;
                var live = new List<ulong>(records.Count);
                foreach (var a in records)
                {
                    // Keeping the address depends on the owner pointer, not on
                    // a lock being held: the slot survives a clear with its
                    // fields zeroed, and dropping it there is what used to
                    // force a full rescan after every release.
                    if (!LockRecord.TryReadSlot(mem, a, scratch, out ulong owner, out var v)) continue;
                    if (owners.TryGetValue(a, out ulong first)) { if (owner != first) continue; }
                    else if (v.Valid) owners[a] = owner;
                    else continue;
                    live.Add(a);
                    if (!v.Valid || v.Uuid == 0 || !UuidShape.IsMonster(v.Uuid)) continue;
                    // Only a target the packet side still knows counts as
                    // proof the record is alive. A freed record that keeps its
                    // last uuid would otherwise refresh this on every poll and
                    // the staleness check below could never fire -- the one
                    // case it exists for.
                    if (Known.Has(v.Uuid)) lastValid.Restart();
                    if (v.LockKind == LockRecord.Kind.Manual)
                    {
                        if (manual == 0) { manual = unchecked((long)v.Uuid); at = a; }
                    }
                    else if (auto == 0)
                    {
                        auto = unchecked((long)v.Uuid);
                    }
                }
                records = live;
                if (records.Count > 0) sticky = new List<ulong>(records);

                // Nothing to poll. Say so instead of reporting a watch that
                // is not happening -- with an empty record list the loop below
                // would publish "watching, nothing locked" every heartbeat,
                // which reads exactly like a player standing next to a monster
                // they have not locked.
                if (records.Count == 0)
                {
                    if (lastUuid != 0)
                    {
                        LogEvent($"uuid 0x{lastUuid:x} -> 0x0 tms={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
                        lastUuid = 0;
                    }
                    WriteState(pid, 0, "waiting_for_lock", $"known={Known.Count}/mon={Known.MonsterCount}");
                    Thread.Sleep(PollMs);
                    continue;
                }

                // Safety net for the case the owner pointer outlives the
                // record it belongs to. Ten idle minutes with a monster list
                // present and never a lock means the address is probably
                // stale, so pay for one scan rather than watch a dead slot.
                if (records.Count > 0 && lastValid.ElapsedMilliseconds >= StaleRecordMs
                    && Known.MonsterCount > 0 && Known.Fresh)
                {
                    LogEvent($"record 0x{records[0]:x} unconfirmed for {StaleRecordMs / 1000}s, rescanning");
                    records.Clear();
                    owners.Clear();
                    lastValid.Restart();
                }

                long uuid = manual;
                if (uuid != lastUuid)
                {
                    bool knownUuid = Known.Has(unchecked((ulong)uuid));
                    // tms is the join key for offline packet correlation: the
                    // packet dump is stamped in unix ms, and the readable clock
                    // here carries no date or zone.
                    LogEvent($"uuid 0x{lastUuid:x} -> 0x{uuid:x} rec=0x{at:x} auto=0x{auto:x}" +
                             (uuid != 0 ? $" known={knownUuid}" : "") +
                             $" tms={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
                    lastUuid = uuid;
                    WriteState(pid, uuid, "watching", DetailFor(at, auto));

                    if (uuid != 0)
                    {
                        if (!sawFirstLock) { sawFirstLock = true; MaybeSnapshot(mem, pid, "first-lock", uuid, at); }
                        else if (Known.Fresh && !knownUuid) MaybeSnapshot(mem, pid, "unknown-uuid", uuid, at);
                    }
                }
                else if (lastBeat.ElapsedMilliseconds >= HeartbeatMs)
                {
                    WriteState(pid, uuid, "watching", DetailFor(at, auto));
                    lastBeat.Restart();
                }

                Thread.Sleep(PollMs);
            }
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    static string DetailFor(ulong recordAddr, long autoUuid) =>
        $"rec=0x{recordAddr:x} auto=0x{autoUuid:x}";

    static bool ProcessExited(int pid)
    {
        try { return Process.GetProcessById(pid).HasExited; }
        catch { return true; }
    }

    // While no record exists there is nothing to poll, so wait and let the
    // outer loop rescan (a lock creates the record).
    internal enum Gate
    {
        // No usable entity list yet, so a scan could not match anything.
        WaitForEntities,
        // A list, but no monsters in it. Nothing to lock, nothing to find.
        NoMonsters,
        Proceed,
    }

    // The first two gates, in the order they have to be asked.
    //
    // It takes the list itself rather than a count, so that the caller cannot
    // hand it the wrong one. It asks for MONSTERS. Measured 2026-09-19 with
    // the entity count here instead: known=8/fresh, all eight of them players
    // and NPCs, and a two second scan every five seconds while standing away
    // from anything attackable. The scan only ever accepts a monster uuid, so
    // a list without one is worth exactly as much as no list -- a distinction
    // an `int` parameter invites a caller to lose.
    internal static Gate CheckGate(bool haveRecord, KnownEntities known)
    {
        int knownMonsters = known.MonsterCount;
        bool knownFresh = known.Fresh;
        // A record already being polled outranks both: it is watched whatever
        // the packet side currently knows, and losing it because the entity
        // list went stale for a moment would cost a full rescan.
        if (haveRecord) return Gate.Proceed;
        if (!knownFresh) return Gate.WaitForEntities;
        if (knownMonsters == 0) return Gate.NoMonsters;
        return Gate.Proceed;
    }

    // When to walk memory looking for the record.
    //
    // Measured 2026-09-19: the record sits at one address for the life of the
    // game process. 0x230d47bb950 held every lock across a fifteen minute
    // session -- through clears, target switches and a zone change -- so once
    // the address is known there is nothing left to search for.
    //
    // The version this replaces rescanned on a timer regardless: every 15 s
    // while a manual lock was held and every 3 s otherwise, which is most of
    // the time. That produced 90 scans of 2.1 s each in seven minutes: 188
    // seconds of all-core scanning underneath the game, a 45% duty cycle,
    // and it is what the stutter was.
    //
    // The entity list gates the rest. The scan only accepts values the packet
    // side names, so without a list every candidate is rejected and the two
    // seconds buy nothing -- 57 of those 90 scans failed exactly that way,
    // including the ones at startup that made the first lock slow to appear.
    internal static bool ShouldScan(bool haveRecord, long msSinceScan, int knownMonsters, bool knownFresh,
        int monstersAtLastScan = int.MaxValue, bool lockKeyPressed = false, int backoffMs = RescanMs)
    {
        if (haveRecord) return false;
        // Monsters, not entities. A list of players and NPCs matches nothing:
        // measured 2026-09-19 as known=8/fresh with cand=0 on every scan.
        if (knownMonsters == 0 || !knownFresh) return false;
        if (msSinceScan < MinScanGapMs) return false;

        // The player just pressed the lock key, so a lock is being held right
        // now -- which is the only state the scan can succeed in, since it
        // matches on the uuid sitting in the slot.
        if (lockKeyPressed) return true;

        // The entity list gained monsters since the scan that failed. That
        // scan could not have matched them, so it is worth another look
        // without waiting out the backoff. This is what the measured failures
        // actually were: three scans over ten seconds with mon going 4, 11,
        // and only the last one finding anything.
        if (knownMonsters > monstersAtLastScan) return true;

        return msSinceScan >= backoffMs;
    }

    // Re-checks addresses that held the record before, applying the same
    // test the scan applies -- a valid record holding a monster the packet
    // side names -- to a handful of addresses instead of to all of memory.
    //
    // The owner pointer is deliberately not compared against the remembered
    // one. That comparison is what the poll uses to notice the record is no
    // longer the same object, and a field transition does exactly that:
    // measured 2026-09-19, the scene is rebuilt and the record comes back at
    // the same address under a new owner. Re-adoption is the other side of
    // that -- it takes the new owner and records it.
    internal static List<ulong> Readopt(IMemSource mem, IEnumerable<ulong> sticky,
        KnownEntities known, byte[] scratch, Dictionary<ulong, ulong> owners)
    {
        var found = new List<ulong>();
        foreach (var a in sticky)
        {
            if (!LockRecord.TryReadSlot(mem, a, scratch, out ulong owner, out var v)) continue;
            if (!v.Valid || !UuidShape.IsMonster(v.Uuid) || !known.Has(v.Uuid)) continue;
            found.Add(a);
            owners[a] = owner;
        }
        return found;
    }

    static bool WaitForRescan(int pid, int iterations)
    {
        for (int i = 0; i < iterations; i++)
        {
            if (ProcessExited(pid)) return false;
            Thread.Sleep(250);
        }
        return true;
    }

    // ---- snapshots -------------------------------------------------------

    // Captures the target's private memory plus the packet-side entity list,
    // so the scan that just succeeded or failed can be re-run offline as many
    // times as an algorithm change needs. Rate limited because a capture
    // costs seconds and stalls the poll loop.
    static void MaybeSnapshot(LiveMem mem, int pid, string reason, long uuid, ulong elem)
    {
        if (SnapshotDir == null || snapshotsTaken >= MaxSnapshots) return;
        if (DateTime.UtcNow - lastSnapshotAt < SnapshotMinGap) return;
        lastSnapshotAt = DateTime.UtcNow;

        try
        {
            Directory.CreateDirectory(SnapshotDir);
            string stem = Path.Combine(SnapshotDir, $"snap-{DateTime.Now:HHmmss}-{reason}");

            // Copy the entity list first: it is the closest-in-time record of
            // what the packet stream believed existed.
            try { File.Copy(EntitiesPath, stem + ".entities.json", true); } catch { }

            LogEvent($"snapshot begin reason={reason} uuid=0x{uuid:x}");
            long bytes = MemSnapshot.Write(mem, stem + ".bin", LogEvent);
            if (bytes < 0) return;

            File.WriteAllText(stem + ".json", JsonSerializer.Serialize(new
            {
                pid,
                reason,
                helperUuid = uuid,
                helperElem = elem,
                capturedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                learnedOwners = learnedOwners.Select(o => $"0x{o:x}").ToArray(),
                learnedVtbls = learnedVtbls.Select(v => $"0x{v:x}").ToArray(),
            }));
            snapshotsTaken++;
            LogEvent($"snapshot done {snapshotsTaken}/{MaxSnapshots} {stem}.bin");
        }
        catch (Exception ex)
        {
            LogEvent($"snapshot failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---- offline replay --------------------------------------------------

    static int Replay(string dir)
    {
        if (!Directory.Exists(dir))
        {
            Console.Error.WriteLine($"no such directory: {dir}");
            return 2;
        }
        var snaps = Directory.GetFiles(dir, "snap-*.bin").OrderBy(x => x).ToArray();
        if (snaps.Length == 0)
        {
            Console.Error.WriteLine($"no snapshots in {dir}");
            return 2;
        }

        Console.WriteLine($"{"snapshot",-34} {"reason",-13} {"expect",-14} {"found",-14} {"known",-6} {"ms",6}  diag");
        int failures = 0;
        foreach (var path in snaps)
        {
            string stem = path[..^4];
            long expect = 0;
            string reason = "";
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(stem + ".json"));
                if (doc.RootElement.TryGetProperty("helperUuid", out var u)) expect = u.GetInt64();
                if (doc.RootElement.TryGetProperty("reason", out var r)) reason = r.GetString() ?? "";
            }
            catch { }

            var known = new KnownEntities(stem + ".entities.json");
            known.Load(forceFresh: true);

            learnedOwners.Clear();
            learnedVtbls.Clear();

            using var mem = new SnapshotMem(path);
            var sw = Stopwatch.StartNew();
            var res = Scanner.ScanRegions(mem, mem.Regions(true), learnedOwners, learnedVtbls, known, null);
            sw.Stop();

            var scratch = new byte[0x48];
            var found = new List<ulong>();
            foreach (var e in res.Elements)
            {
                if (Scanner.TryReadTarget(mem, e, scratch, out long u, out _) && u != 0)
                {
                    found.Add(unchecked((ulong)u));
                }
            }
            var distinct = found.Distinct().ToList();
            bool hit = expect == 0 || distinct.Contains(unchecked((ulong)expect));
            int bogus = distinct.Count(u => !known.Has(u));
            bool pass = hit && bogus == 0;
            if (!pass) failures++;

            string foundStr = distinct.Count == 0 ? "(none)"
                : distinct.Count == 1 ? $"0x{distinct[0]:x}"
                : $"0x{distinct[0]:x}+{distinct.Count - 1}";

            Console.WriteLine($"{Path.GetFileName(path),-34} {reason,-13} 0x{expect,-12:x} {foundStr,-14} " +
                              $"{(bogus == 0 ? "ok" : $"+{bogus}!"),-6} {sw.ElapsedMilliseconds,6}  {res.Diag} " +
                              $"[{(pass ? "PASS" : "FAIL")}]");
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? $"PASS {snaps.Length}/{snaps.Length}"
            : $"FAIL {failures}/{snaps.Length}");
        return failures == 0 ? 0 : 1;
    }

    // Structural survey across a recorded session. The container the lock
    // list uses is generic, so knowing which of the candidates is the real
    // one needs the candidates laid out side by side across several moments.
    // Prints every candidate element with its owner and the entity behind
    // each occupied slot, then groups by owner so a list whose contents track
    // the player's target can be told from one that merely holds whatever is
    // nearby.
    static int DumpElements(string dir)
    {
        var snaps = Directory.GetFiles(dir, "snap-*.bin").OrderBy(x => x).ToArray();
        if (snaps.Length == 0)
        {
            Console.Error.WriteLine($"no snapshots in {dir}");
            return 2;
        }

        var byOwner = new Dictionary<ulong, List<string>>();
        foreach (var path in snaps)
        {
            string stem = path[..^4];
            long expect = 0;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(stem + ".json"));
                if (doc.RootElement.TryGetProperty("helperUuid", out var u)) expect = u.GetInt64();
            }
            catch { }

            var known = new KnownEntities(stem + ".entities.json");
            known.Load(forceFresh: true);
            learnedOwners.Clear();
            learnedVtbls.Clear();

            using var mem = new SnapshotMem(path);
            var dump = new List<string>();
            var res = Scanner.ScanRegions(mem, mem.Regions(true), learnedOwners, learnedVtbls, known, dump);

            Console.WriteLine($"== {Path.GetFileName(path)}  helperSaid=0x{expect:x} " +
                              $"self=0x{known.SelfUuid:x} entities={known.Count} {res.Diag}");
            foreach (var line in dump)
            {
                Console.WriteLine("   " + line);
                int oi = line.IndexOf("owner=0x", StringComparison.Ordinal);
                if (oi < 0) continue;
                int end = line.IndexOf(' ', oi);
                if (end < 0) end = line.Length;
                if (ulong.TryParse(line[(oi + 8)..end], System.Globalization.NumberStyles.HexNumber, null, out ulong owner))
                {
                    if (!byOwner.TryGetValue(owner, out var l)) byOwner[owner] = l = new List<string>();
                    l.Add(Path.GetFileName(path));
                }
            }
            Console.WriteLine();
        }

        Console.WriteLine("== owners seen across snapshots ==");
        foreach (var kv in byOwner.OrderByDescending(k => k.Value.Count))
        {
            Console.WriteLine($"   owner=0x{kv.Key:x} in {kv.Value.Distinct().Count()}/{snaps.Length} snapshots, {kv.Value.Count} elements");
        }
        return 0;
    }

    // Differential hunt for the field that actually holds the lock-on target.
    //
    // The container the scanner has been chasing turned out to be the scene's
    // entity registry: every candidate had one occupied slot, one shared
    // owner, and held whatever happened to be nearby -- zones, bullets, the
    // player. So the target is somewhere else.
    //
    // Addresses are stable inside one game process, and the snapshots of a
    // session all come from one. An address that holds a different monster
    // uuid in different snapshots, and always one the packet stream says is
    // present, is a field that tracks a changing monster. The registry nodes
    // hold one uuid for their whole life, so they drop out automatically.
    static int FindTargetField(string dir)
    {
        var snaps = Directory.GetFiles(dir, "snap-*.bin").OrderBy(x => x).ToArray();
        if (snaps.Length < 2)
        {
            Console.Error.WriteLine($"need at least 2 snapshots in {dir}");
            return 2;
        }

        var perSnap = new List<Dictionary<ulong, ulong>>();
        var labels = new List<string>();

        foreach (var path in snaps)
        {
            string stem = path[..^4];
            var known = new KnownEntities(stem + ".entities.json");
            known.Load(forceFresh: true);

            var hits = new Dictionary<ulong, ulong>();
            using (var mem = new SnapshotMem(path))
            {
                var buf = new byte[Scanner.ChunkSize];
                foreach (var (b, s) in mem.Regions(true))
                {
                    ulong pos = 0;
                    while (pos < s)
                    {
                        int want = (int)Math.Min((ulong)Scanner.ChunkSize, s - pos);
                        if (!mem.Read(b + pos, buf, want, out int read) || read <= 0)
                        {
                            pos += (ulong)want;
                            continue;
                        }
                        for (int off = 0; off + 8 <= read; off += 8)
                        {
                            ulong v = Scanner.U64(buf, off);
                            if (UuidShape.IsMonster(v) && known.Has(v))
                            {
                                hits[b + pos + (ulong)off] = v;
                            }
                        }
                        pos += (ulong)read;
                    }
                }
            }
            perSnap.Add(hits);
            labels.Add(Path.GetFileName(path));
            Console.WriteLine($"{Path.GetFileName(path)}: {hits.Count} addresses hold a live monster uuid");
            GC.Collect();
        }

        // Keep addresses seen in several snapshots whose value actually moves.
        var seen = new Dictionary<ulong, List<(int snap, ulong uuid)>>();
        for (int i = 0; i < perSnap.Count; i++)
        {
            foreach (var kv in perSnap[i])
            {
                if (!seen.TryGetValue(kv.Key, out var l)) seen[kv.Key] = l = new();
                l.Add((i, kv.Value));
            }
        }

        var candidates = seen
            .Where(kv => kv.Value.Count >= 3 && kv.Value.Select(x => x.uuid).Distinct().Count() >= 2)
            .OrderByDescending(kv => kv.Value.Select(x => x.uuid).Distinct().Count())
            .ThenByDescending(kv => kv.Value.Count)
            .Take(40)
            .ToList();

        Console.WriteLine();
        Console.WriteLine($"addresses tracked: {seen.Count}");
        Console.WriteLine($"candidates (>=3 snapshots, >=2 distinct monsters): {candidates.Count}");
        Console.WriteLine();
        foreach (var kv in candidates)
        {
            var vals = string.Join(" ", kv.Value.Select(x => $"[{x.snap}]0x{x.uuid:x}"));
            Console.WriteLine($"0x{kv.Key:x}  distinct={kv.Value.Select(x => x.uuid).Distinct().Count()}  {vals}");
        }

        Console.WriteLine();
        for (int i = 0; i < labels.Count; i++) Console.WriteLine($"  [{i}] {labels[i]}");
        return 0;
    }

    // Dumps the memory around given addresses in every snapshot, so the object
    // a candidate target field lives in can be identified and given a
    // signature that survives a restart (absolute addresses do not).
    static int InspectAddr(string dir, string[] addrArgs)
    {
        var addrs = new List<ulong>();
        foreach (var a in addrArgs)
        {
            string s = a.StartsWith("0x") ? a[2..] : a;
            if (ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out ulong v)) addrs.Add(v);
        }
        if (addrs.Count == 0) { Console.Error.WriteLine("no addresses"); return 2; }

        var snaps = Directory.GetFiles(dir, "snap-*.bin").OrderBy(x => x).ToArray();
        const int Before = 0x40, After = 0x60;
        foreach (var path in snaps)
        {
            string stem = path[..^4];
            var known = new KnownEntities(stem + ".entities.json");
            known.Load(forceFresh: true);
            using var mem = new SnapshotMem(path);
            Console.WriteLine($"== {Path.GetFileName(path)}");
            foreach (var a in addrs)
            {
                Console.WriteLine($"  -- around 0x{a:x}");
                var buf = new byte[Before + After];
                if (!mem.Read(a - Before, buf, buf.Length, out int got) || got < buf.Length)
                {
                    Console.WriteLine("     (unreadable)");
                    continue;
                }
                for (int off = 0; off + 8 <= buf.Length; off += 8)
                {
                    ulong v = Scanner.U64(buf, off);
                    int rel = off - Before;
                    string note = "";
                    if (UuidShape.IsPlausible(v)) note = $"  uuid {UuidShape.Describe(v)} \"{known.Describe(v)}\"";
                    else if (Scanner.LooksLikeHeapPtr(v)) note = "  ptr";
                    Console.WriteLine($"     {(rel < 0 ? "-" : "+")}0x{Math.Abs(rel):x2}  0x{v:x16}{note}{(rel == 0 ? "   <== " : "")}");
                }
            }
            Console.WriteLine();
        }
        return 0;
    }

    // Offline: find the address whose entity tracks the player's marks.
    //
    // Each sweep is labelled by what the player did at that instant, so an
    // address qualifies only if it agrees with every mark: it holds a monster
    // while the player reports a lock, and stops holding one when they report
    // clearing it. That rules out the fields the first session could not --
    // attack target, aggro, boss list -- because the marks happen while the
    // player is deliberately not attacking anything.
    internal static int DiffMarks(string dir)
    {
        var files = Directory.GetFiles(dir, "mark-*.tsv.gz").OrderBy(x => x).ToArray();
        if (files.Length < 2)
        {
            Console.Error.WriteLine($"need at least 2 mark sweeps in {dir}");
            return 2;
        }

        var kinds = new List<string>();
        var labels = new List<string>();
        // address -> per-sweep uuid (0 = not present in that sweep)
        var byAddr = new Dictionary<ulong, ulong[]>();
        // Memory around each confirmed hit, for finding the flag that says
        // whether the lock is the manual one or the automatic provisional one.
        var windows = new List<Dictionary<ulong, byte[]>>();

        for (int i = 0; i < files.Length; i++)
        {
            string name = Path.GetFileName(files[i]);
            string kind = name.Split('-').ElementAtOrDefault(2) ?? "?";
            kinds.Add(kind);
            labels.Add(name);

            var win = new Dictionary<ulong, byte[]>();
            windows.Add(win);
            using var fs = File.OpenRead(files[i]);
            using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);
            using var r = new StreamReader(gz);
            string? line;
            int n = 0;
            while ((line = r.ReadLine()) != null)
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var p = line.Split('\t');
                if (p.Length < 3) continue;
                if (!ulong.TryParse(p[1], System.Globalization.NumberStyles.HexNumber, null, out ulong addr)) continue;
                if (p[0] == "W")
                {
                    try { win[addr] = Convert.FromHexString(p[2]); } catch { }
                    continue;
                }
                if (!ulong.TryParse(p[2], System.Globalization.NumberStyles.HexNumber, null, out ulong uuid)) continue;
                // Only values the packet stream named count as a lock. Shape
                // alone admits bit patterns by the tens of thousands, and one
                // of them holding a monster-shaped constant across every mark
                // would otherwise outrank the real field.
                bool confirmed = p.Length < 4 || p[3] == "1";
                if (!confirmed) continue;
                // Prefix the kind so a value hit and a pointer hit at the same
                // address stay distinct.
                ulong key = p[0] == "P" ? addr | 0x8000_0000_0000_0000UL : addr;
                if (!byAddr.TryGetValue(key, out var arr))
                {
                    byAddr[key] = arr = new ulong[files.Length];
                }
                arr[i] = uuid;
                n++;
            }
            Console.WriteLine($"[{i}] {name}  kind={kind}  hits={n}");
        }

        // Measured behaviour: the field keeps the last locked uuid after the
        // player clears the lock, so absence at a clear mark cannot be
        // required -- an earlier version demanded it and threw away the
        // answer. Clear marks stay informative (printed below) but only lock
        // marks decide.
        var scored = new List<(ulong Key, int Score, ulong[] Vals)>();
        foreach (var kv in byAddr)
        {
            var v = kv.Value;
            int locks = 0;
            bool disqualified = false;
            for (int i = 0; i < v.Length; i++)
            {
                if (kinds[i] != "lock") continue;
                if (v[i] != 0 && UuidShape.IsMonster(v[i])) locks++;
                else disqualified = true;
            }
            if (disqualified) continue;
            // A field that never changes is a registry node, not a target.
            int distinct = v.Where(x => x != 0).Distinct().Count();
            if (distinct < 2) continue;
            scored.Add((kv.Key, distinct * 100 + locks, v));
        }

        Console.WriteLine();
        Console.WriteLine($"addresses tracked: {byAddr.Count}");
        Console.WriteLine($"holding a different monster at each lock mark: {scored.Count}");
        Console.WriteLine();
        foreach (var s in scored.OrderByDescending(x => x.Score).Take(40))
        {
            bool isPtr = (s.Key & 0x8000_0000_0000_0000UL) != 0;
            ulong addr = s.Key & 0x7FFF_FFFF_FFFF_FFFFUL;
            var cells = string.Join(" ", s.Vals.Select((u, i) => $"[{i}:{kinds[i][0]}]" + (u == 0 ? "-" : $"0x{u:x}")));
            Console.WriteLine($"{(isPtr ? "P" : "V")} 0x{addr:x}  {cells}");
        }
        if (scored.Count == 0)
        {
            Console.WriteLine("no address tracked the lock marks. Either the marks were pressed");
            Console.WriteLine("too late, the same enemy was locked every time, or there were no");
            Console.WriteLine("monsters in range -- check the per-sweep hit counts above first.");
        }

        ReportLockKindFlag(kinds, windows,
            scored.OrderByDescending(x => x.Score).Take(10)
                  .Select(x => x.Key & 0x7FFF_FFFF_FFFF_FFFFUL));
        return 0;
    }

    // Finds the byte that says which kind of lock is held.
    //
    // The game keeps one target slot shared by two states: the provisional
    // lock it takes automatically on whatever comes into range, and the
    // manual lock the player sets on purpose. Only the manual one is worth
    // showing -- an automatic lock follows whatever wanders into range, so a
    // display driven by the uuid alone would wander with it. Each sweep
    // stores a window around every confirmed hit; an offset that reads one
    // way at every manual mark and another at every automatic mark is the
    // flag.
    private static void ReportLockKindFlag(List<string> kinds, List<Dictionary<ulong, byte[]>> windows,
        IEnumerable<ulong> candidates)
    {
        var manual = Enumerable.Range(0, kinds.Count).Where(i => kinds[i] == "lock").ToArray();
        var auto = Enumerable.Range(0, kinds.Count).Where(i => kinds[i] == "auto").ToArray();
        Console.WriteLine();
        if (manual.Length == 0 || auto.Length == 0)
        {
            Console.WriteLine("lock-kind flag: needs both manual (Ctrl+Alt+M) and automatic");
            Console.WriteLine($"(Ctrl+Alt+B) marks in one run; this has {manual.Length} manual, {auto.Length} automatic.");
            return;
        }

        Console.WriteLine("== byte separating a manual lock from the automatic one ==");
        int found = 0;
        foreach (var addr in candidates)
        {
            for (int off = 0; off < MarkScan.WindowSize && found < 40; off++)
            {
                byte? m0 = null, a0 = null;
                bool ok = true;
                foreach (int i in manual)
                {
                    if (!windows[i].TryGetValue(addr, out var w) || w.Length <= off) { ok = false; break; }
                    if (m0 == null) m0 = w[off];
                    else if (w[off] != m0) { ok = false; break; }
                }
                if (!ok || m0 == null) continue;
                foreach (int i in auto)
                {
                    if (!windows[i].TryGetValue(addr, out var w) || w.Length <= off) { ok = false; break; }
                    if (a0 == null) a0 = w[off];
                    else if (w[off] != a0) { ok = false; break; }
                }
                if (!ok || a0 == null || a0 == m0) continue;

                int rel = off - MarkScan.WindowBefore;
                Console.WriteLine($"  uuid at 0x{addr:x}, flag at {(rel < 0 ? "-" : "+")}0x{Math.Abs(rel):x2}: " +
                                  $"manual=0x{m0:x2} auto=0x{a0:x2}");
                found++;
            }
        }
        if (found == 0)
        {
            Console.WriteLine("  none found. The kind may live away from the uuid, or a mark was");
            Console.WriteLine("  pressed while the state was not what it claimed.");
        }
    }

    // Runs the lock-record signature over whole-memory snapshots from a
    // session it was not derived from, to see what it picks up there.
    static int TestSignature(string dir)
    {
        var snaps = Directory.GetFiles(dir, "snap-*.bin").OrderBy(x => x).ToArray();
        if (snaps.Length == 0)
        {
            Console.Error.WriteLine($"no snapshots in {dir}");
            return 2;
        }
        foreach (var path in snaps)
        {
            string stem = path[..^4];
            var known = new KnownEntities(stem + ".entities.json");
            known.Load(forceFresh: true);
            using var mem = new SnapshotMem(path);
            var sw = Stopwatch.StartNew();
            var hits = LockRecord.Find(mem, known, out string diag);
            sw.Stop();

            // How much the packet-side entity list is actually doing. The
            // live scan fails whenever the locked monster is not in it yet --
            // measured 2026-09-19 as known=18/mon=4 on a scan that found
            // nothing. If dropping that filter still leaves exactly one
            // match, the filter is costing more than it earns.
            var loose = LockRecord.Find(mem, known, out string looseDiag, requireKnown: false);

            var scratch = new byte[LockRecord.ReadSize];
            Console.WriteLine($"{Path.GetFileName(path),-34} entities={known.Count,-4} {diag} ms={sw.ElapsedMilliseconds}");
            Console.WriteLine($"{"",-34} without the entity filter: {looseDiag}");
            foreach (var a in loose)
            {
                if (hits.Contains(a)) continue;
                if (LockRecord.TryRead(mem, a, scratch, out var lv))
                {
                    Console.WriteLine($"    EXTRA 0x{a:x} {UuidShape.Describe(lv.Uuid)} [{known.Describe(lv.Uuid)}] {lv.LockKind}");
                }
            }
            foreach (var a in hits)
            {
                if (LockRecord.TryRead(mem, a, scratch, out var v))
                {
                    Console.WriteLine($"    0x{a:x} {UuidShape.Describe(v.Uuid)} [{known.Describe(v.Uuid)}] {v.LockKind}");
                }
            }
        }
        return 0;
    }

    // ---- selftest --------------------------------------------------------

    // Round-trips a deterministic pattern through the snapshot format using
    // this process's own memory. Verifies addressing, compression and the
    // stitching across the writer's 32 MiB block boundaries, all without the
    // game running.
    static int SelfTest()
    {
        const int PatternSize = 48 * 1024 * 1024; // spans a block boundary
        var pattern = new byte[PatternSize];
        var rng = new Random(20260919);
        rng.NextBytes(pattern);
        var pin = GCHandle.Alloc(pattern, GCHandleType.Pinned);
        try
        {
            ulong addr = (ulong)pin.AddrOfPinnedObject().ToInt64();
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_VM_READ, false, Environment.ProcessId);
            if (h == IntPtr.Zero)
            {
                Console.Error.WriteLine($"selftest: OpenProcess(self) failed {Marshal.GetLastWin32Error()}");
                return 1;
            }
            var mem = new LiveMem(h);
            string file = Path.Combine(Path.GetTempPath(), "BPSR-Radar", "selftest.bin");
            Console.WriteLine($"selftest: pattern at 0x{addr:x} size={PatternSize >> 20} MiB");
            long bytes = MemSnapshot.Write(mem, file, s => Console.WriteLine("  " + s));
            Native.CloseHandle(h);
            if (bytes < 0) { Console.Error.WriteLine("selftest: capture refused"); return 1; }

            using var snap = new SnapshotMem(file);
            int bad = 0, checks = 0;
            var buf = new byte[4096];
            var probe = new Random(1234);
            for (int i = 0; i < 400; i++)
            {
                int off = probe.Next(0, PatternSize - buf.Length);
                checks++;
                if (!snap.Read(addr + (ulong)off, buf, buf.Length, out int got) || got != buf.Length)
                {
                    bad++;
                    continue;
                }
                if (!buf.AsSpan().SequenceEqual(pattern.AsSpan(off, buf.Length))) bad++;
            }
            // Explicitly cross a writer block boundary.
            int boundary = 32 * 1024 * 1024 - 1024;
            var span = new byte[4096];
            checks++;
            if (!snap.Read(addr + (ulong)boundary, span, span.Length, out int n2) || n2 != span.Length
                || !span.AsSpan().SequenceEqual(pattern.AsSpan(boundary, span.Length)))
            {
                bad++;
                Console.WriteLine("  block-boundary read MISMATCH");
            }

            try { File.Delete(file); } catch { }
            Console.WriteLine(bad == 0 ? $"selftest: PASS ({checks} probes)" : $"selftest: FAIL ({bad}/{checks} mismatched)");
            return bad == 0 ? 0 : 1;
        }
        finally
        {
            pin.Free();
        }
    }

    // ---- state file ------------------------------------------------------

    static void WriteState(int pid, long uuid, string state, string? detail = null)
    {
        lock (stateLock)
        {
            curPid = pid; curUuid = uuid; curState = state; curDetail = detail;
            WriteStateFile(pid, uuid, state, detail);
        }
    }

    static void WriteStateFile(int pid, long uuid, string state, string? detail)
    {
        try
        {
            Directory.CreateDirectory(WorkDir);
            string tmp = StatePath + ".tmp";
            var payload = new
            {
                pid,
                uuid,
                state,
                d = detail,
                cfg = ConfigStamp,
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            File.WriteAllText(tmp, JsonSerializer.Serialize(payload));
            File.Move(tmp, StatePath, true);
        }
        catch { }
    }
}
