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

    // Asks which pages the target already has resident. Needs no access right
    // beyond the PROCESS_QUERY_INFORMATION the handle is already opened with,
    // and changes nothing in the target -- it is a question, not a request.
    [DllImport("psapi.dll", SetLastError = true)]
    public static extern bool QueryWorkingSetEx(IntPtr hProcess, IntPtr info, int cb);

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
    // How often the record is re-read once its address is known. The read is
    // one 0x40 byte ReadProcessMemory per watched address, so the rate is not
    // bounded by what it costs -- it is bounded by how fast the overlay has
    // to react. At 100ms a target held for less than a tenth of a second was
    // never sampled at all, which is what cycling quickly through locks looks
    // like from the overlay: entries missing outright rather than late.
    const int PollMs = 25;
    const int HeartbeatMs = 2000;

    // Housekeeping does not run on the poll's budget. ProcessExited walks the
    // machine's process list and StopRequested stats a file; at the poll rate
    // those would cost far more than the memory read the loop exists for, and
    // neither answer changes meaningfully within a quarter second. Keeping
    // them on the same clock as the read is what used to cap the poll rate.
    const int HousekeepMs = 250;

    // How long the loop parks itself when there is nothing it can do yet --
    // no usable entity list, or a scan that just failed. It is not a backoff:
    // MinScanGapMs and the doubling backoff below decide when the next scan
    // is allowed. All this controls is how soon the loop looks again, so an
    // urgent trigger (the lock key, or monsters appearing in the list) is
    // acted on rather than slept through. It used to be 500ms and 1000ms,
    // which is most of the delay after a field transition.
    const int GateWaitMs = 100;

    // How often the shut gate explains itself. Rare enough not to fill the
    // log while a player stands in a town for ten minutes.
    const int GateLogMs = 10000;

    // The entity list's reload interval, by whether it is on the critical
    // path. Settled: a record is held and the list is barely consulted.
    // Urgent: no record, and nothing can proceed until the list refills.
    const int SettledEntityRefreshMs = 2000;
    const int UrgentEntityRefreshMs = 200;

    // Floor between scans however urgent the trigger, so a held key or a
    // list filling one entity at a time cannot turn into a scan loop.
    const int MinScanGapMs = 400;

    // No ceiling on the cost-proportional part of the floor.
    //
    // There was one, at 2000ms, added because a 15.2s scan had been followed
    // by 15.3s of nothing. But capping the wait uncaps the duty: a 15s scan
    // with a 2s gap is 88% of the wall clock spent in all-core
    // ReadProcessMemory under the game, against 50% with no cap. Stutter is
    // the worse failure, and the wait was never the real problem -- the 15.2s
    // scan was, and LockRecord.Find no longer walks freed memory a page at a
    // time. A scan that slow again is a symptom, and backing off hard is the
    // right response to it, not a shorter wait.

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
    internal static int RescanMsForTests => RescanMs;
    internal static int MaxRescanMsForTests => MaxRescanMs;

    // The backoff rule, extracted so a test can drive it rather than model
    // it. Every scan widens the gap; only a manual lock -- the one piece of
    // evidence that the record set is the right one -- resets it. Resetting
    // on a merely successful scan is what pinned the scanner at a 50% duty
    // cycle while a player fought without manually locking.
    internal static int BackoffAfterScan(int backoff) => Math.Min(backoff * 2, MaxRescanMs);
    internal static int BackoffAfterManualLock() => RescanMs;

    // The backoff after an acquiring press that produced nothing. A floor,
    // not a reset: see UnansweredRescanMs. Extracted so the duty-cycle test
    // drives this rather than restating it -- a test that restates the rule
    // passes against a call site that has stopped using it.
    internal static int BackoffAfterUnansweredPress(int backoff) =>
        Math.Max(backoff, UnansweredRescanMs);

    // The key or mouse button the player locks with, as a virtual-key code.
    // Zero disables the trigger. The game's default is the middle mouse
    // button, but the binding varies and a controller has none at all, which
    // is why this is one of three ways a scan gets started rather than the
    // only one.
    static int LockKeyVk;
    static bool lockKeyWasDown;

    // The edge test, split out so it can be exercised without a keyboard.
    //
    // Two independent signals, because bit 15 alone does not work here:
    //   bit 15  the key is down at this instant
    //   bit 0   the key went down at some point since the previous call
    //
    // The loop cannot sample continuously. A scan blocks it for a second or
    // more (14.7s in the worst measured case) and the wait paths used to
    // sleep half a second at a time without sampling at all, so a middle
    // click -- fifty milliseconds of contact -- almost never coincided with
    // a sample. Measured 2026-09-19: 227 scans in one session, with
    // LockKeyVk=4 configured and reaching the helper, and not one of them
    // triggered by the key. Bit 0 is what catches a press that began and
    // ended while the loop was busy elsewhere.
    //
    // Bit 0 is best-effort, not dependable: the async key state is shared,
    // and whoever reads it first clears it -- the game itself, or another
    // overlay polling the same virtual key, can take the press before this
    // loop sees it. That is why it is ORed with the bit 15 edge rather than
    // replacing it. When bit 0 is stolen the test degrades to exactly the
    // behaviour it replaced, which the dense sampling below now covers; it
    // never does worse.
    internal static bool IsLockKeyEdge(short state, bool wasDown, out bool nowDown)
    {
        nowDown = (state & 0x8000) != 0;
        bool pressedSinceLastCall = (state & 0x0001) != 0;
        return pressedSinceLastCall || (nowDown && !wasDown);
    }

    // True on the press, not while held.
    static bool LockKeyPressed()
    {
        if (LockKeyVk == 0) return false;
        bool edge = IsLockKeyEdge(Native.GetAsyncKeyState(LockKeyVk), lockKeyWasDown, out bool down);
        lockKeyWasDown = down;
        return edge;
    }

    // A press is latched rather than acted on where it is seen. Sampling and
    // deciding happen at different points in the loop, and a press that
    // arrives while the scan gap has not expired must not be forgotten -- it
    // means "scan as soon as you are allowed to", not "scan only if you
    // happen to be allowed to right now". Cleared by the scan that consumes
    // it, and by nothing else.
    static bool pendingLockPress;

    // When the last press was seen, on the same clock the poll uses. Separate
    // from the latch above because the two answer different questions: the
    // latch asks "should the scanner look now", this asks "did the set the
    // loop is already watching answer the press". Cleared by the answer.
    static long lastPressAtMs;

    // Every press, counted. The loop logs the ones it has not logged yet.
    //
    // The press was already being sampled forty times a second and was
    // already the thing the whole feature turns on, but it only ever reached
    // the log indirectly -- as "(lock key)" on a scan it happened to trigger,
    // or in the unanswered dump. So the log could say the overlay showed "-"
    // and could not say whether the player was trying to lock anything at the
    // time, which is the one question it needs a human for. It does not: the
    // answer was already in the process, unrecorded.
    static int lockPressCount;

    // Narrow first. Every record the eight-snapshot corpus has ever held was
    // in a 16 MiB heap segment, and reading only those costs the game a
    // fifteenth of the resident memory (see Scanner.HeapSegmentSize). It is a
    // guess about one allocator, though, so an empty narrow result widens to
    // what the scan always did rather than reporting "nothing is locked" --
    // the worst case is the old cost, never a missed record.
    //
    // This lives in one function, and the test drives this function, so that
    // deleting the fallback fails a test instead of only failing in the field.
    internal static List<ulong> FindWithFallback(IMemSource mem, KnownEntities known,
        out string diag, IDictionary<ulong, ulong>? owners = null,
        bool mayReadColdPages = true)
    {
        var found = LockRecord.Find(mem, known, out diag, owners: owners,
                                    pass: ScanPass.Narrow);
        if (found.Count > 0) return found;

        // The heap-segment guess broke in the field (2026-09-19 23:26:45: the
        // narrow pass found nothing and a full pass found the record), so the
        // next rung drops it. It still reads only resident pages, so it costs
        // time and not a megabyte of the player's memory.
        var resident = LockRecord.Find(mem, known, out string resDiag, owners: owners,
                                       pass: ScanPass.Resident);
        diag = $"{diag} | {resDiag}";
        if (resident.Count > 0) return resident;

        if (!mayReadColdPages) return resident;

        var all = LockRecord.Find(mem, known, out string allDiag, owners: owners,
                                  pass: ScanPass.Everything);
        diag = $"{diag} | {allDiag}";
        return all;
    }

    // Whether this scan is allowed the pass that reads pages the game does not
    // have resident -- the only pass that can grow the game's memory.
    //
    // It used to run whenever the cheap pass came up empty, which sounded
    // careful and was the opposite. Measured over one session: 22 of 26 scans
    // widened, and in 21 of those 22 the expensive pass ALSO found nothing.
    // Of course it did -- nothing was locked. Reading twelve gigabytes to
    // confirm that a player who is not locking anything has no lock record
    // took the game from 5,587 MiB to 12,752 MiB.
    //
    // "The cheap pass found nothing" and "there is nothing to find" are the
    // same observation, so the trigger cannot be that. It has to be evidence
    // that a record OUGHT to exist, and the only such evidence the helper has
    // is the player pressing the lock key and getting nothing for it. The
    // cooldown is there because that evidence can repeat faster than the scan.
    internal const int ColdPassCooldownMs = 60_000;

    internal static bool MayReadColdPages(bool unansweredPress, long msSinceColdPass) =>
        unansweredPress && msSinceColdPass >= ColdPassCooldownMs;

    // The game's resident memory, which this tool changes: reading a page
    // through ReadProcessMemory faults it into the TARGET's working set and it
    // does not come back on its own. Logged either side of a scan so the cost
    // shows up in the log instead of only in the player's Task Manager.
    // Returns 0 rather than throwing -- it is diagnostics, not a dependency.
    static long TargetWorkingSetMiB(int pid)
    {
        try { return System.Diagnostics.Process.GetProcessById(pid).WorkingSet64 >> 20; }
        catch { return 0; }
    }

    // Whether the overlay was showing nothing when the last press landed, and
    // the value it was showing. See UnansweredPressesToDistrust.
    static bool pressWhileDark;
    static long lastShownUuid;

    static void SampleLockKey()
    {
        if (LockKeyPressed())
        {
            pendingLockPress = true;
            lockPressCount++;
            lastPressAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            pressWhileDark = lastShownUuid == 0;
        }
    }

    // How long after a press a manual lock should have turned up in the
    // watched set. A press moves the lock immediately; the poll runs every
    // PollMs, so this is generous by an order of magnitude and only fires
    // when the manual lock went somewhere the loop is not looking.
    const int LockAnswerMs = 750;

    // How many unanswered presses in a row before the set stops being
    // trusted.
    //
    // This was two, because the same key clears a lock and that press
    // correctly produces nothing -- with no way to tell the two apart, one
    // would have fired on every release. There is a way now. A press made
    // while the overlay was already showing a target is a clear or a switch;
    // a press made while it was showing nothing is the player trying to
    // acquire, and that one producing nothing is an anomaly on its own.
    //
    // Measured 2026-09-19:
    //   21:43:06.398  key press showing=0x1a9b10040  -> cleared. Not counted.
    //   21:43:08.896  key press showing=0x0          -> nothing for 6.5 s,
    //                 until the player pressed again at 21:43:15.453 and the
    //                 target appeared 140 ms later.
    //
    // Waiting for a second press is what made that six and a half seconds:
    // the player supplied it by hand. One is enough.
    //
    // It is NOT enough to tell a miss from a whiff. A player who presses lock
    // with nothing in range gets no manual lock either, and from memory the
    // two are identical -- the helper cannot see what is in front of the
    // player, and in the measured case the automatic lock was empty too, so
    // there is no discriminator. A whiff therefore costs a scan, and the only
    // defence is to bound what that scan costs.
    const int UnansweredPressesToDistrust = 1;

    // The floor the backoff is raised to when an acquiring press goes
    // unanswered, so that a player whiffing repeatedly cannot pull the
    // scanner back up to the duty cycle this whole line of work removed.
    //
    // A manual lock resets the backoff to RescanMs (750 ms), so without this
    // a whiff during ordinary play would be answered by a scan about a second
    // later: scan(~1 s) + gap(~1 s), 50%. At two seconds the ceiling is 33%
    // even if every press whiffs, and the scan still arrives about two
    // seconds after the press instead of waiting for the player to notice and
    // press again.
    const int UnansweredRescanMs = 2000;

    // How many addresses to remember. The game was measured holding six lock
    // records at once (2026-09-19 15:00:03, rec=6), and each costs one 0x40
    // byte read per re-check, so the cap is about bounding a runaway rather
    // than a real budget.
    const int StickyCap = 64;

    // How often remembered addresses that are not in the live set are
    // re-checked. On the housekeeping clock, not the poll clock.
    const int RemergeMs = 250;

    // How many times the watch loop may be restarted after an unexpected
    // exception before the helper gives up and reports the error.
    const int MaxWatchRestarts = 5;

    // Bounds for the unanswered-press dump. See the poll.
    const int UnansweredLogMs = 30000;
    const int MaxUnansweredLines = 8;

    // Throttle for the signature-disagreement line. It fires on a per-target
    // condition, so it would otherwise repeat forty times a second for as
    // long as that target is locked.
    const int SignatureLogMs = 10000;
    // How long a watched address may go without ever holding a lock before it
    // is treated as stale.
    //
    // This was ten minutes, on the reasoning that a player can stand around
    // for a while and a rescan was expensive. Both halves were wrong. A scan
    // is now under a second, and ten minutes is not a safety net at all: on
    // 2026-09-19 a zone into the guild hall left the helper polling a record
    // the new scene had freed -- the owner pointer survived in the freed
    // memory, so the owner check passed -- and because it still "had" a
    // record it never scanned. Three and a half minutes of a dead slot, the
    // overlay stuck at "-", and the session ended before the timer fired.
    //
    // The scene id below is the real fix; this is the backstop for a record
    // that goes dead without a zone change.
    const int StaleRecordMs = 60 * 1000;
    internal static int StaleRecordMsForTests => StaleRecordMs;

    // How long the loop may hold records without publishing anything before
    // it says so. The failure above was three and a half minutes in which
    // nothing was written to the log at all, because every branch that logs
    // needs something to change.
    const int IdleLogMs = 30000;
    internal static int GateLogMsForTests => GateLogMs;
    internal static int IdleLogMsForTests => IdleLogMs;
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
        if (args.Length >= 2 && args[0] == "--survey-records")
        {
            return SurveyRecords(args[1]);
        }
        if (args.Length >= 2 && args[0] == "--scan-cost")
        {
            return ScanCost(args[1]);
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

        // An unexpected exception used to end the helper. It is worth saying
        // exactly how badly that failed, because the two halves compound: the
        // helper exited leaving "error: ..." in its state file, and the app's
        // error branch returns before the branch that relaunches a missing
        // helper -- so the feature stayed dead until the game itself was
        // restarted. Measured 2026-09-19 at 20:24:15; the app went on writing
        // entity exports for another hour to a helper that no longer existed.
        //
        // The watch loop owns no state that survives it, so restarting it is
        // clean: it re-opens the process and rediscovers the record. Bounded,
        // because a fault that repeats is a bug to be found in the log rather
        // than spun on.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return Run(pid);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
                if (attempt >= MaxWatchRestarts || ProcessExited(pid))
                {
                    WriteState(pid, 0, "error: " + ex.Message);
                    LogEvent($"error {ex.GetType().Name}: {ex.Message} (giving up after {attempt})");
                    return 1;
                }
                LogEvent($"error {ex.GetType().Name}: {ex.Message} (restarting watch, attempt {attempt})");
                WriteState(pid, 0, "restarting after error");
                Thread.Sleep(500);
            }
        }
    }

    // What build this is, for the log. The release number is chosen at
    // release time, so during development every build says 0.0.0-dev and the
    // commit hash is the only thing that tells two of them apart -- which is
    // exactly what a bug report needs. A log that cannot name its build
    // cannot be compared with another one.
    internal static string BuildId()
    {
        var asm = System.Reflection.Assembly.GetEntryAssembly();
        var info = asm is null ? null
            : System.Reflection.CustomAttributeExtensions
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(asm);
        return info?.InformationalVersion ?? asm?.GetName().Version?.ToString() ?? "unknown";
    }

    static int Run(int pid)
    {
        LogEvent($"helper build {BuildId()}");
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero)
        {
            WriteState(pid, 0, $"error: OpenProcess failed {Marshal.GetLastWin32Error()}");
            return 1;
        }

        var mem = new LiveMem(h);
        var scratch = new byte[LockRecord.ReadSize];
        // Only the analysis modes print entity names, and the list is now
        // reloaded five times a second while there is no record.
        Known.KeepNames = SnapshotDir != null;

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
            // The last time a pass was allowed to read pages the game did not
            // have resident. See MayReadColdPages.
            var sinceColdPass = Stopwatch.StartNew();
            bool coldPassRan = false;
            int lockableAtLastScan = int.MaxValue;
            int backoff = RescanMs;
            // What the last scan cost, which sets the floor before the next
            // one however urgent the trigger. See ShouldScan.
            long lastScanMs = 0;
            // Whether the current record set has ever produced a manual lock,
            // and the automatic lock seen on the previous pass. Together they
            // decide whether the set is trusted -- see RecordSetTrusted.
            bool manualSeen = false;
            long lastAuto = 0;
            var lastValid = Stopwatch.StartNew();
            var idleLog = Stopwatch.StartNew();
            // Paces the re-check of remembered addresses that are missing
            // from the live set. See RemergeMs.
            var remerge = Stopwatch.StartNew();
            // Presses that produced no manual lock, in a row. See
            // UnansweredPressesToDistrust.
            int unansweredPresses = 0;
            var signatureLog = Stopwatch.StartNew();
            var unansweredLog = Stopwatch.StartNew();
            int loggedPressCount = lockPressCount;
            int lastRemergeLogged = -1;
            // The scene the watched record was adopted in. A zone change frees
            // and rebuilds the scene, so a record from the previous one is
            // gone however intact its owner pointer still looks.
            uint recordScene = 0;
            // Not `new Stopwatch()`: an unstarted one reads zero forever, so
            // the >= GateLogMs test below never passes and Restart() is never
            // reached. That is exactly what shipped -- the log from the run
            // that finally caught the guild hall defect contains no gate line
            // at all, including across 27 seconds of a shut gate in a town.
            // A diagnostic that cannot fire is worse than none: its silence
            // reads as evidence that the state never occurred.
            var gateLog = Stopwatch.StartNew();
            bool gateWasOpen = true;

            for (;;)
            {
                // The expensive part of an iteration, on its own slower clock
                // (see HousekeepMs). MarkScan.ReadNew re-parses the whole mark
                // file every call, so it belongs here too -- and a mark is a
                // deliberate keypress against a state the player is holding,
                // not a transient the sweep can miss by starting a fraction of
                // a second later.
                if (HousekeepDue())
                {
                    if (ProcessExited(pid)) { LogEvent("game exited"); return 0; }
                    if (StopRequested()) { LogEvent("stop requested by the app"); return 0; }

                    // Ground-truth sweeps: the player marks the instant they
                    // lock or clear a target, and memory is sampled right then.
                    if (SnapshotDir != null)
                    {
                        foreach (var mark in MarkScan.ReadNew(MarksPath, lastMarkSeq))
                        {
                            lastMarkSeq = Math.Max(lastMarkSeq, mark.N);
                            MarkScan.Sweep(mem, Known, Path.Combine(SnapshotDir, "marks"), mark, LogEvent);
                        }
                    }
                }

                // Adaptive, for the same reason the tick rates are. While a
                // record is held the entity list is barely used and two
                // seconds is right. While there is no record it is the
                // critical path: a field transition clears the packet side's
                // list, and nothing -- not re-adoption, not a scan -- can
                // happen until this file says a monster exists again. Holding
                // that behind a two second throttle put up to two seconds
                // into every transition.
                Known.Refresh(records.Count > 0 ? SettledEntityRefreshMs : UrgentEntityRefreshMs);

                // Sampled on every pass, whichever branch the loop takes
                // below. This used to happen at one point only, which the
                // wait paths skipped entirely.
                SampleLockKey();

                // Before the gate, so a press is recorded whichever branch
                // the iteration takes -- including the gated one, where
                // nothing else writes a line at all. Coalesced to one line per
                // pass, which bounds it at the poll rate however the key is
                // held or spammed.
                if (lockPressCount != loggedPressCount)
                {
                    int n = lockPressCount - loggedPressCount;
                    loggedPressCount = lockPressCount;
                    LogEvent($"key press{(n > 1 ? $" x{n}" : "")} showing=0x{lastUuid:x}" +
                             $" records={records.Count}/{sticky.Count}" +
                             $" known={Known.Count}/tgt={Known.LockableCount}");
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
                    if (gate == Gate.NoTargets && lastUuid != 0)
                    {
                        LogEvent($"uuid 0x{lastUuid:x} -> 0x0 tms={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
                        lastUuid = 0;
                    }
                    PublishState(pid, 0, gate == Gate.NoTargets ? "no_targets" : "waiting_for_entities",
                        $"known={Known.Count}/tgt={Known.LockableCount}");
                    // A shut gate is the one state that produced no log line
                    // at all, so a report of "nothing ever appears here"
                    // arrived with no evidence attached. The type histogram
                    // separates "the packet side has seen nothing" from "it
                    // has, but of types the helper does not accept" -- which
                    // is the whole of the guild hall defect.
                    // On the way in, and every GateLogMs while it stays
                    // shut. A gate that closes for five seconds has to leave
                    // a trace too.
                    if (gateWasOpen || gateLog.ElapsedMilliseconds >= GateLogMs)
                    {
                        gateWasOpen = false;
                        gateLog.Restart();
                        LogEvent($"gate {(gate == Gate.NoTargets ? "no_targets" : "waiting_for_entities")}" +
                                 $" known={Known.Count}/tgt={Known.LockableCount} fresh={Known.Fresh}" +
                                 $" types=[{Known.TypeHistogram()}]");
                    }
                    if (!WaitForRescan(pid, GateWaitMs)) return 0;
                    continue;
                }
                gateWasOpen = true;

                // A zone change invalidates the record even when every check
                // below still passes: the owner pointer lives in freed memory
                // and keeps its value. Nothing the helper can see in the
                // game's memory says the world was rebuilt, so the packet
                // side has to tell it. Readopt runs next and usually takes
                // the same address straight back -- the record is measured to
                // land at the same place after a transition -- so this costs
                // one read per remembered address, not a scan.
                if (Known.Fresh && Known.SceneId != 0 && records.Count > 0 && Known.SceneId != recordScene)
                {
                    LogEvent($"scene {recordScene}->{Known.SceneId}, dropping {records.Count} record(s) to re-verify");
                    records.Clear();
                    owners.Clear();
                    lastValid.Restart();
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
                        manualSeen = false;
                        idleLog.Restart();
                        recordScene = Known.SceneId;
                        lastValid.Restart();
                        LogEvent($"record 0x{again[0]:x} back without a scan ({again.Count} of {sticky.Count} remembered)");
                    }
                }
                // Holding some of the remembered addresses but not all of
                // them. That is the normal outcome of any single re-adoption
                // or scan, because both can only recognise a record that
                // holds a lock right now and the game keeps several -- the
                // manual one reads empty whenever nothing is manually locked.
                // So keep asking for the rest instead of settling for the
                // subset that happened to be busy at one instant.
                else if (records.Count > 0 && records.Count < sticky.Count
                         && remerge.ElapsedMilliseconds >= RemergeMs)
                {
                    remerge.Restart();
                    var missing = new List<ulong>();
                    foreach (var a in sticky) if (!records.Contains(a)) missing.Add(a);
                    var extra = Readopt(mem, missing, Known, scratch, owners);
                    if (extra.Count > 0)
                    {
                        records.AddRange(extra);
                        records.Sort();
                        // Only when the size actually moves. An address that
                        // flapped -- re-adopted here, dropped by the poll, and
                        // back again -- would otherwise write four lines a
                        // second into a log two processes append to.
                        if (records.Count != lastRemergeLogged)
                        {
                            lastRemergeLogged = records.Count;
                            LogEvent($"record set +{extra.Count} (now {records.Count} of {sticky.Count} remembered)");
                        }
                    }
                }

                // The backoff applies between scans, not before the first one.
                long sinceScan = scannedOnce ? lastScan.ElapsedMilliseconds : long.MaxValue;
                bool pressed = pendingLockPress;
                bool trusted = RecordSetTrusted(manualSeen, lastAuto);
                // The press skips the backoff only when there is no record at
                // all -- the state where the scan is the only way forward and
                // a press is proof a lock is being held for it to match. With
                // a record in hand the same shortcut would put an unbounded
                // number of scans under a player who taps the key, and the
                // only thing left holding the duty cycle down would be the
                // scan-length floor: 50% for a one second scan.
                bool urgent = pressed && records.Count == 0;
                if (ShouldScan(records.Count > 0 && trusted, sinceScan, Known.LockableCount, Known.Fresh,
                        lockableAtLastScan, urgent, backoff, lastScanMs))
                {
                    // Consumed here and only here, so a press that arrived
                    // during the scan gap survives until a scan can use it.
                    pendingLockPress = false;
                    var sw = Stopwatch.StartNew();
                    // Owners are deliberately NOT cleared here any more. They
                    // are the evidence that lets re-adoption take an address
                    // back without waiting for it to hold a lock, and a scan
                    // only refreshes the ones it can see -- which is never the
                    // whole set. Clearing them threw that evidence away on
                    // every scan. The scene-change branch still clears them,
                    // which is the case they genuinely go stale in.
                    long wsBefore = TargetWorkingSetMiB(pid);
                    // `pressed` is the player asking for a lock and not having
                    // got one; that, and only that, buys the expensive pass.
                    bool mayReadCold = MayReadColdPages(
                        pressed || unansweredPresses > 0,
                        coldPassRan ? sinceColdPass.ElapsedMilliseconds : long.MaxValue);
                    var found = FindWithFallback(mem, Known, out string diag, owners, mayReadCold);
                    if (mayReadCold && diag.Contains("everything"))
                    {
                        sinceColdPass.Restart();
                        coldPassRan = true;
                    }
                    sw.Stop();
                    long wsAfter = TargetWorkingSetMiB(pid);
                    // What the scan cost the game, in the units the player
                    // sees in Task Manager. Read pages are faulted into the
                    // target's working set and do not leave on their own.
                    if (wsBefore > 0 || wsAfter > 0)
                        diag += $" ws={wsBefore}->{wsAfter}MiB";
                    lastScanMs = sw.ElapsedMilliseconds;
                    lastScan.Restart();
                    scannedOnce = true;
                    lockableAtLastScan = Known.LockableCount;
                    records = found;
                    manualSeen = false;
                    // Or the first idle line lands milliseconds after the
                    // scan and claims the set has been quiet for thirty
                    // seconds. Measured at 19:41:52.778, two milliseconds
                    // after the scan that produced the record it names.
                    idleLog.Restart();
                    if (found.Count > 0) Remember(sticky, found);

                    if (records.Count == 0)
                    {
                        backoff = BackoffAfterScan(backoff);
                        LogEvent($"scan ms={sw.ElapsedMilliseconds} {diag} known={Known.Count}/tgt={Known.LockableCount}" +
                                 (pressed ? " (lock key)" : "") + $" next={backoff}ms");
                        PublishState(pid, 0, "waiting_for_lock", diag);
                        if (lastUuid != 0)
                        {
                            LogEvent($"uuid 0x{lastUuid:x} -> 0x0 tms={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
                            lastUuid = 0;
                        }
                        if (!WaitForRescan(pid, GateWaitMs)) return 0;
                        continue;
                    }
                    // NOT reset here. A scan that succeeds has found *a*
                    // record set, which is not the same as having found the
                    // right one: while only an automatic lock exists the set
                    // is untrusted (see RecordSetTrusted) and ShouldScan will
                    // ask again. Resetting the backoff on success turned that
                    // into scan(~1s) + gap(~1s) forever -- a 50% duty cycle
                    // for as long as a player fights without manually
                    // locking, which is worse than the 45% this whole line of
                    // work removed. The backoff now grows on every scan and
                    // is reset only by an actual manual lock below, which is
                    // the only evidence that the set is the right one.
                    backoff = BackoffAfterScan(backoff);
                    recordScene = Known.SceneId;
                    lastValid.Restart();
                    LogEvent($"scan ms={sw.ElapsedMilliseconds} {diag} records={records.Count} scene={recordScene}");
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
                    if (!LockRecord.TryReadSlot(mem, a, scratch, out ulong owner, out var v, out var raw)) continue;
                    if (owners.TryGetValue(a, out ulong first)) { if (owner != first) continue; }
                    else if (v.Valid) owners[a] = owner;
                    else continue;
                    live.Add(a);

                    // Read the fields, do not re-run the discovery test.
                    //
                    // The signature exists to pick one address out of
                    // gigabytes of heap. Applying it to an address that has
                    // already been identified -- and whose owner pointer says
                    // it is still the same object -- adds nothing and can only
                    // subtract: any target whose record happens to fail one of
                    // the incidental parts of the signature reads as "nothing
                    // is locked". That is silent, per-target, and recovers
                    // when the player locks something else, which is exactly
                    // the report ("a few individuals show -, re-locking
                    // sometimes fixes it").
                    //
                    // What still has to hold is what identifies the record and
                    // the value: the owner pointer, checked above, a uuid the
                    // game could actually have locked, and a kind field that
                    // is one of the two kinds.
                    if (!PollAccepts(raw)) continue;

                    // And say so when the two disagree, because this is the
                    // measurement that decides whether the paragraph above is
                    // right. The poll used to reject here and write nothing,
                    // so the log recorded the consequence (manual stayed 0)
                    // and never the cause.
                    if (!v.Valid && signatureLog.ElapsedMilliseconds >= SignatureLogMs)
                    {
                        signatureLog.Restart();
                        LogEvent($"record 0x{a:x} holds 0x{raw.Uuid:x} but the scan signature rejects it: " +
                                 LockRecord.Explain(scratch));
                    }

                    // Only a target the packet side still knows counts as
                    // proof the record is alive. A freed record that keeps its
                    // last uuid would otherwise refresh this on every poll and
                    // the staleness check below could never fire -- the one
                    // case it exists for.
                    if (Known.Has(raw.Uuid)) lastValid.Restart();
                    if (raw.KindWord == 1)
                    {
                        if (manual == 0) { manual = unchecked((long)raw.Uuid); at = a; }
                    }
                    else if (auto == 0)
                    {
                        auto = unchecked((long)raw.Uuid);
                    }
                }
                records = live;
                if (records.Count > 0) Remember(sticky, records);

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
                    PublishState(pid, 0, "waiting_for_lock", $"known={Known.Count}/tgt={Known.LockableCount}");
                    Thread.Sleep(PollMs);
                    continue;
                }

                // Safety net for the case the owner pointer outlives the
                // record it belongs to. Ten idle minutes with a monster list
                // present and never a lock means the address is probably
                // stale, so pay for one scan rather than watch a dead slot.
                if (records.Count > 0 && lastValid.ElapsedMilliseconds >= StaleRecordMs
                    && Known.LockableCount > 0 && Known.Fresh)
                {
                    LogEvent($"record 0x{records[0]:x} unconfirmed for {StaleRecordMs / 1000}s, rescanning");
                    records.Clear();
                    owners.Clear();
                    lastValid.Restart();
                    manualSeen = false;
                    // And stop the iteration here. Everything below polls the
                    // set that was just declared dead: it published "watching"
                    // from an empty set, and the idle diagnostic indexed
                    // records[0] on it.
                    //
                    // That was not a race. Both clocks are restarted at the
                    // same instant -- the last poll that saw a known uuid
                    // restarts lastValid, and the last poll that saw a manual
                    // lock restarts idleLog -- so the 60s expiry always lands
                    // on a pass where the 30s idle line is due as well.
                    // Measured 2026-09-19: idle at 20:23:45.605, staleness at
                    // 20:24:15.620, ArgumentOutOfRangeException 4 ms later,
                    // and the helper exited. Every session that idles a minute
                    // with a record and monsters in view hit it.
                    if (lastUuid != 0)
                    {
                        LogEvent($"uuid 0x{lastUuid:x} -> 0x0 tms={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
                        lastUuid = 0;
                    }
                    PublishState(pid, 0, "waiting_for_lock", $"known={Known.Count}/tgt={Known.LockableCount}");
                    Thread.Sleep(PollMs);
                    continue;
                }

                lastAuto = auto;
                if (manual != 0)
                {
                    // Proof the set holds the record the game writes manual
                    // locks into, and the only thing that earns a reset.
                    manualSeen = true;
                    backoff = BackoffAfterManualLock();
                    unansweredPresses = 0;
                    lastPressAtMs = 0;
                }
                else if (lastPressAtMs != 0
                         && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastPressAtMs >= LockAnswerMs)
                {
                    // The player pressed lock and the watched set never showed
                    // a manual one. Trust was a latch: manualSeen was set once
                    // and never cleared, so a set that stopped receiving
                    // manual locks -- because it had shrunk, or because the
                    // game started using a record found in no scan -- was
                    // still "the" set and ShouldScan returned false for the
                    // rest of the process. Measured 2026-09-19: ninety seconds
                    // of locking after 20:22:45 with not one scan.
                    //
                    // Distrust only reopens scanning; it does not bypass the
                    // backoff, so the duty cycle stays where the backoff puts
                    // it.
                    lastPressAtMs = 0;
                    // A press made while a target was on screen is a clear or
                    // a switch; it is allowed to produce nothing.
                    if (pressWhileDark) unansweredPresses++;
                    // Dump what the watched records actually held. This is
                    // the moment the player pressed lock and the overlay said
                    // nothing, so it is the one sample worth the bytes -- and
                    // it separates "the record is empty, the lock went
                    // somewhere we are not watching" from "the record holds
                    // the target and something here threw it away".
                    //
                    // Bounded on both axes, because the condition is not rare.
                    // The lock key is also the key that clears a lock, and on
                    // some setups the middle mouse button does something else
                    // entirely -- every one of those presses is "unanswered".
                    // Unbounded this wrote one line per watched record (up to
                    // StickyCap) every LockAnswerMs, into a file two processes
                    // append to.
                    if (unansweredLog.ElapsedMilliseconds >= UnansweredLogMs)
                    {
                        unansweredLog.Restart();
                        int shown = 0;
                        foreach (var a in records)
                        {
                            if (shown++ >= MaxUnansweredLines)
                            {
                                LogEvent($"  unanswered: ... and {records.Count - MaxUnansweredLines} more");
                                break;
                            }
                            if (!LockRecord.TryReadSlot(mem, a, scratch, out ulong o, out _, out var r)) continue;
                            LogEvent($"  unanswered: 0x{a:x} owner=0x{o:x} uuid=0x{r.Uuid:x} " +
                                     $"kind=0x{r.KindWord:x} [{LockRecord.Explain(scratch)}]");
                        }
                    }
                    if (DistrustAfterUnansweredPresses(manualSeen, unansweredPresses))
                    {
                        unansweredPresses = 0;
                        manualSeen = false;
                        backoff = BackoffAfterUnansweredPress(backoff);
                        LogEvent($"{UnansweredPressesToDistrust} acquiring press unanswered by " +
                                 $"{records.Count} record(s) of {sticky.Count} remembered; distrusting the set");
                    }
                }

                long uuid = manual;
                bool changed = uuid != lastUuid;
                bool knownUuid = changed && Known.Has(unchecked((ulong)uuid));
                if (changed)
                {
                    // tms is the join key for offline packet correlation: the
                    // packet dump is stamped in unix ms, and the readable clock
                    // here carries no date or zone.
                    LogEvent($"uuid 0x{lastUuid:x} -> 0x{uuid:x} rec=0x{at:x} auto=0x{auto:x}" +
                             (uuid != 0 ? $" known={knownUuid}" : "") +
                             $" tms={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
                    lastUuid = uuid;
                }
                lastShownUuid = uuid;

                // Unconditional, and cheap when nothing changed: PublishState
                // returns without touching the disk unless the reading moved
                // or the heartbeat came due. It runs before the snapshot below
                // on purpose -- a snapshot stalls this loop for seconds, and
                // the overlay should already have the target by then.
                PublishState(pid, uuid, "watching", DetailFor(at, auto));

                // The silence that hid the guild hall defect. Holding records
                // and publishing nothing looks exactly like standing next to
                // something you have not locked, and neither wrote a line.
                if (uuid != 0) idleLog.Restart();
                else if (idleLog.ElapsedMilliseconds >= IdleLogMs)
                {
                    idleLog.Restart();
                    LogEvent(IdleLine(records, sticky.Count, recordScene, Known.Count,
                        Known.LockableCount, lastValid.ElapsedMilliseconds, auto,
                        RecordSetTrusted(manualSeen, auto)));
                }

                if (changed && uuid != 0)
                {
                    if (!sawFirstLock) { sawFirstLock = true; MaybeSnapshot(mem, pid, "first-lock", uuid, at); }
                    else if (Known.Fresh && !knownUuid) MaybeSnapshot(mem, pid, "unknown-uuid", uuid, at);
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
        // A list, but nothing lockable in it. Training dummies count:
        // they are EntDummy, not EntMonster, and the guild hall has only
        // those, which held this gate shut there forever.
        NoTargets,
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
        int knownLockable = known.LockableCount;
        bool knownFresh = known.Fresh;
        // A record already being polled outranks both: it is watched whatever
        // the packet side currently knows, and losing it because the entity
        // list went stale for a moment would cost a full rescan.
        if (haveRecord) return Gate.Proceed;
        if (!knownFresh) return Gate.WaitForEntities;
        if (knownLockable == 0) return Gate.NoTargets;
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
    // Whether the record set in hand can be trusted to carry a manual lock.
    //
    // A scan matches on the uuid sitting in a slot, so it can only find a
    // record that holds a lock at that instant. The game keeps the manual
    // lock and the automatic provisional one in separate records, and the
    // manual one reads as empty while nothing is manually locked -- so a scan
    // that runs while only the automatic lock exists finds the automatic
    // record and not the manual one. After that the loop has "a record",
    // ShouldScan stops looking, and every later manual lock is written to an
    // address nobody is watching.
    //
    // Measured 2026-09-19 in the guild hall: scan found records=2 at
    // 19:42:52, then auto=0xb30040 (Elite Guardian Dummy) held steady for two
    // minutes forty-six seconds with manual never leaving zero. The same hall
    // worked at 19:20 for the one reason that the scan there happened to run
    // while a manual lock was held.
    //
    // So a set that has never produced a manual lock is provisional, and
    // stays open to rescanning while an automatic lock is visible -- that
    // being the state in which a manual record exists to be found and is
    // demonstrably not in the set. An empty slot is left alone: nothing is
    // locked, so a scan would match nothing and the old no-timer-rescans
    // property holds.
    internal static bool RecordSetTrusted(bool manualSeen, long autoUuid) => manualSeen || autoUuid == 0;

    // The idle diagnostic, as its own function so the empty-set case can be
    // asserted. It used to read records[0] unguarded, inside a loop body that
    // a branch above could reach with the set already cleared -- see the
    // staleness drop. That is the ArgumentOutOfRangeException that killed the
    // helper at 20:24:15.624 on 2026-09-19.
    internal static string IdleLine(List<ulong> records, int remembered, uint scene,
        int known, int lockable, long lastValidMs, long auto, bool trusted) =>
        $"idle records={records.Count}/{remembered}" +
        $" rec=0x{(records.Count > 0 ? records[0] : 0):x} scene={scene}" +
        $" known={known}/tgt={lockable}" +
        $" lastValid={lastValidMs / 1000}s auto=0x{auto:x}" +
        $" trusted={trusted}";

    // Whether a set that has been answering manual locks should stop being
    // believed. Trust used to be a one-way latch, which is what let a shrunken
    // set hold the loop closed to scanning for a whole session.
    //
    // Two presses, not one: the same key clears a lock, and that press
    // correctly produces no manual lock.
    internal static bool DistrustAfterUnansweredPresses(bool manualSeen, int unanswered) =>
        manualSeen && unanswered >= UnansweredPressesToDistrust;

    // What a watched record has to hold for the poll to read a target out of
    // it. Not the scan signature: see the poll for why.
    internal static bool PollAccepts(LockRecord.Raw raw) =>
        raw.Uuid != 0 && UuidShape.IsLockable(raw.Uuid) && raw.KindWord <= 1;

    internal static bool ShouldScan(bool haveRecord, long msSinceScan, int knownLockable, bool knownFresh,
        int lockableAtLastScan = int.MaxValue, bool lockKeyPressed = false, int backoffMs = RescanMs,
        long lastScanMs = 0)
    {
        if (haveRecord) return false;
        // Monsters, not entities. A list of players and NPCs matches nothing:
        // measured 2026-09-19 as known=8/fresh with cand=0 on every scan.
        if (knownLockable == 0 || !knownFresh) return false;

        // The floor is whichever is longer: the fixed gap, or however long
        // the previous scan actually took. lastScan is restarted when a scan
        // finishes, so this bounds the scanner at half the wall clock however
        // it is triggered, with no exception for a scan that ran long.
        //
        // It matters now in a way it did not before. The urgent triggers below
        // skip the backoff, and the lock key had never once fired (measured
        // 2026-09-19: 227 scans, none attributed to it), so nothing tested
        // what a player repeatedly pressing lock in a field where the record
        // cannot be found would do. With a fixed 400ms floor and a two second
        // scan that is an 83% duty cycle of all-core ReadProcessMemory under
        // the game -- worse than the 45% that this whole line of work was
        // about removing.
        if (msSinceScan < Math.Max(MinScanGapMs, lastScanMs)) return false;

        // The player just pressed the lock key, so a lock is being held right
        // now -- which is the only state the scan can succeed in, since it
        // matches on the uuid sitting in the slot.
        if (lockKeyPressed) return true;

        // The entity list gained monsters since the scan that failed. That
        // scan could not have matched them, so it is worth another look
        // without waiting out the backoff. This is what the measured failures
        // actually were: three scans over ten seconds with mon going 4, 11,
        // and only the last one finding anything.
        if (knownLockable > lockableAtLastScan) return true;

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
            if (!LockRecord.TryReadSlot(mem, a, scratch, out ulong owner, out var v, out var raw)) continue;
            // Two ways to be accepted, and the second is why this is not one
            // condition. Holding a lock the packet side names identifies a
            // record on its own -- that is the test the scan itself applies,
            // and it is the only one available for an address whose owner
            // was never recorded (a scene change clears them). But an address
            // already confirmed as a record under this exact owner pointer
            // does not have to prove itself again by holding a lock.
            //
            // Demanding it did is measured damage. On 2026-09-19 a scan found
            // three records at 20:19:32; re-adoption at 20:22:45 ran while
            // one of them held a lock and kept only that one, and the other
            // two -- empty at that instant, which is the normal state of a
            // record nothing is locked in -- were dropped. Every later manual
            // lock the game wrote into either of them was invisible: the
            // overlay sat at "-" for fifteen seconds and then sixty while the
            // player was locking monsters.
            bool sameObject = owners.TryGetValue(a, out ulong first) && owner == first;
            // Same split as the poll: an address confirmed under this owner is
            // read, an unconfirmed one has to pass the discovery test to earn
            // its place.
            if (!sameObject && !(v.Valid && UuidShape.IsLockable(raw.Uuid) && known.Has(raw.Uuid))) continue;
            if (sameObject && !Scanner.LooksLikeHeapPtr(owner)) continue;
            found.Add(a);
            owners[a] = owner;
        }
        return found;
    }

    // Addresses that ever held a record, so re-adoption has something to try
    // before a scan. It only grows.
    //
    // It used to be overwritten with the live set on every poll
    // (`sticky = new List<ulong>(records)`), which quietly made it the
    // *current* set rather than a memory of the set. Combined with the
    // re-adoption rule above that was a one-way ratchet: 20:19:32 remembered
    // three addresses, 20:22:45 re-adopted one of them, and the poll a
    // millisecond later reduced the memory to that one. Nothing could ever
    // widen it again -- the set had produced a manual lock, so it counted as
    // trusted and ShouldScan never ran again for the rest of the session.
    internal static void Remember(List<ulong> sticky, IEnumerable<ulong> addrs, int cap = StickyCap)
    {
        foreach (var a in addrs)
        {
            if (a == 0 || sticky.Contains(a)) continue;
            sticky.Add(a);
            if (sticky.Count > cap) sticky.RemoveAt(0);
        }
    }

    // Sleeps in poll-sized slices instead of quarter-second blocks, so the
    // lock key is still sampled while the loop is parked. A press landing in
    // here used to vanish: the edge test only ran at one point in the loop,
    // which this path skips.
    //
    // ProcessExited keeps its own slower cadence. It walks the machine's
    // process list and has no business running at the sampling rate.
    // Shared with the main loop on purpose. It used to be local and seeded to
    // fire immediately, so every call -- and the gate path calls this ten
    // times a second -- walked the machine's process list on its first
    // iteration. That is the cost the housekeeping split exists to avoid, and
    // the split was not achieving it.
    static readonly Stopwatch housekeep = Stopwatch.StartNew();
    static bool housekeepPrimed;

    static bool HousekeepDue()
    {
        if (housekeepPrimed && housekeep.ElapsedMilliseconds < HousekeepMs) return false;
        housekeepPrimed = true;
        housekeep.Restart();
        return true;
    }

    static bool WaitForRescan(int pid, int totalMs)
    {
        var sw = Stopwatch.StartNew();
        for (;;)
        {
            long now = sw.ElapsedMilliseconds;
            if (now >= totalMs) return true;
            if (HousekeepDue() && ProcessExited(pid)) return false;
            SampleLockKey();
            Thread.Sleep((int)Math.Min(PollMs, totalMs - now));
        }
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

    // Measures the two fields the scan signature calls "positions".
    //
    // The poll stopped trusting that signature on 2026-09-19 after a live
    // sample showed it rejecting a record that plainly held a lock. The scan
    // still uses it, so the same values decide whether a record can be FOUND
    // at all -- and two samples are not enough to change a predicate on. This
    // walks the recorded snapshots with the position test removed and prints
    // every field it would otherwise have judged, so the predicate can be
    // rewritten against measured data instead of a guess.
    // What the narrow pass costs and what it misses, over the whole corpus.
    // The question it answers is not "is it faster" but "does reading a
    // fifteenth of the memory still find every record" -- a miss here is a
    // target the overlay would show as `-`, so the pass/fail is the record
    // sets being equal, and the megabytes are the reason to care.
    static int ScanCost(string dir)
    {
        if (!Directory.Exists(dir))
        {
            Console.Error.WriteLine($"no such directory: {dir}");
            return 2;
        }
        var snaps = Directory.GetFiles(dir, "snap-*.bin", SearchOption.AllDirectories)
                             .OrderBy(x => x).ToArray();
        if (snaps.Length == 0)
        {
            Console.Error.WriteLine($"no snapshots under {dir}");
            return 2;
        }

        Console.WriteLine($"{"snapshot",-30} {"narrow",-26} {"everything",-26} same?");
        ulong narrowBytes = 0, wideBytes = 0;
        int checked_ = 0, agreed = 0;
        foreach (var path in snaps)
        {
            string stem = path[..^4];
            var known = new KnownEntities(stem + ".entities.json");
            known.Load(forceFresh: true);
            if (known.Count == 0) continue;

            using var mem = new SnapshotMem(path);
            var regions = mem.Regions(privateOnly: true);
            foreach (var r in regions)
            {
                wideBytes += r.Size;
                if (Scanner.HeapSegment(r.Size)) narrowBytes += r.Size;
            }

            var n = LockRecord.Find(mem, known, out string nDiag, pass: ScanPass.Narrow);
            var w = LockRecord.Find(mem, known, out string wDiag, pass: ScanPass.Everything);
            n.Sort(); w.Sort();
            bool same = n.Count == w.Count;
            for (int i = 0; same && i < n.Count; i++) same = n[i] == w[i];
            checked_++;
            if (same) agreed++;
            Console.WriteLine($"{Path.GetFileNameWithoutExtension(path),-30} {nDiag,-26} {wDiag,-26} " +
                              (same ? "yes" : $"NO  narrow missed {string.Join(",", w.Except(n).Select(x => $"0x{x:x}"))}"));
        }

        Console.WriteLine();
        Console.WriteLine($"snapshots where the two passes agree: {agreed}/{checked_}");
        Console.WriteLine($"bytes read: narrow {narrowBytes / (1024.0 * 1024 * 1024):F2} GiB, " +
                          $"everything {wideBytes / (1024.0 * 1024 * 1024):F2} GiB " +
                          $"({(wideBytes == 0 ? 0 : 100.0 * (1 - (double)narrowBytes / wideBytes)):F1}% less)");
        return agreed == checked_ && checked_ > 0 ? 0 : 1;
    }

    static int SurveyRecords(string dir)
    {
        if (!Directory.Exists(dir))
        {
            Console.Error.WriteLine($"no such directory: {dir}");
            return 2;
        }
        var snaps = Directory.GetFiles(dir, "snap-*.bin", SearchOption.AllDirectories)
                             .OrderBy(x => x).ToArray();
        if (snaps.Length == 0)
        {
            Console.Error.WriteLine($"no snapshots under {dir}");
            return 2;
        }

        int total = 0, doubleishBoth = 0, shippedOk = 0, nonZeroFlags = 0;
        Console.WriteLine($"{"snapshot",-30} {"addr",-14} {"uuid",-12} {"k",1} " +
                          $"{"A raw",-18} {"A f32 lo/hi",-22} {"B raw",-18} {"B f32 lo/hi",-22} dbl?");
        foreach (var path in snaps)
        {
            string stem = path[..^4];
            var known = new KnownEntities(stem + ".entities.json");
            known.Load(forceFresh: true);
            if (known.Count == 0) continue;

            using var mem = new SnapshotMem(path);
            foreach (var (addr, win) in SweepRelaxed(mem, known))
            {
                ulong a = Scanner.U64(win, 0x20);
                ulong b = Scanner.U64(win, 0x28);
                ulong uuid = Scanner.U64(win, 0x08);
                ulong kind = Scanner.U64(win, 0x38);
                bool zeros = Scanner.U64(win, 0x10) == 0 && Scanner.U64(win, 0x18) == 0
                             && Scanner.U64(win, 0x30) == 0;
                bool both = zeros && Scanner.Doubleish(a) && Scanner.Doubleish(b);
                bool shipped = zeros && Scanner.PositionField(a) && Scanner.PositionField(b);
                if (shipped) shippedOk++;
                if (!zeros) nonZeroFlags++;
                total++;
                if (both) doubleishBoth++;
                Console.WriteLine($"{Path.GetFileNameWithoutExtension(path),-30} 0x{addr:x10} " +
                    $"0x{uuid:x8}   {kind} " +
                    $"0x{a:x16} {F32(a),-22} 0x{b:x16} {F32(b),-22} {(both ? "yes" : "NO")}");
            }
        }
        Console.WriteLine();
        Console.WriteLine($"records found with the position test removed: {total}");
        Console.WriteLine($"  of those, the current signature would accept: {doubleishBoth}");
        Console.WriteLine($"  it would have MISSED: {total - doubleishBoth}");
        Console.WriteLine($"  the SHIPPED signature accepts: {shippedOk}");
        Console.WriteLine($"  records whose 'zero' qwords are int32 flags instead: {nonZeroFlags}");
        return 0;
    }

    // The survey relaxes the "must be zero" qwords the same way it relaxes the
    // positions: a live sample on 2026-09-19 at 21:21:18 had z1=0x100000001,
    // z2=0x100000000, z3=0x100000001 -- pairs of int32 flags, not zeros. The
    // corpus sweep could not have seen that, because it demanded zeros.
    internal static bool ZeroOrFlags(ulong v) =>
        (uint)v <= 1 && (uint)(v >> 32) <= 1;

    static string F32(ulong v)
    {
        float lo = BitConverter.Int32BitsToSingle(unchecked((int)(uint)v));
        float hi = BitConverter.Int32BitsToSingle(unchecked((int)(uint)(v >> 32)));
        return $"{lo:g6}/{hi:g6}";
    }

    // LockRecord.Find's inner test with the two position checks removed and
    // nothing else changed.
    static IEnumerable<(ulong Addr, byte[] Window)> SweepRelaxed(IMemSource mem, KnownEntities known)
    {
        var buf = new byte[Scanner.ChunkSize];
        foreach (var (b, s) in mem.Regions(privateOnly: true))
        {
            ulong pos = 0;
            while (pos < s)
            {
                int want = (int)Math.Min((ulong)Scanner.ChunkSize, s - pos);
                if (!mem.Read(b + pos, buf, want, out int read) || read <= 0) { pos += 0x1000; continue; }
                for (int off = LockRecord.Before; off + (LockRecord.ReadSize - LockRecord.Before) <= read; off += 8)
                {
                    ulong v = Scanner.U64(buf, off);
                    if (!UuidShape.IsLockable(v) || !known.Has(v)) continue;
                    int w = off - LockRecord.Before;
                    if (!Scanner.LooksLikeHeapPtr(Scanner.U64(buf, w))) continue;
                    if (!ZeroOrFlags(Scanner.U64(buf, w + 0x10))) continue;
                    if (!ZeroOrFlags(Scanner.U64(buf, w + 0x18))) continue;
                    if (!ZeroOrFlags(Scanner.U64(buf, w + 0x30))) continue;
                    if (Scanner.U64(buf, w + 0x38) > 1) continue;
                    var win = new byte[LockRecord.ReadSize];
                    Array.Copy(buf, w, win, 0, LockRecord.ReadSize);
                    yield return (b + pos + (ulong)off, win);
                }
                if ((ulong)read >= s - pos) break;
                pos += (ulong)Math.Max(8, (read - LockRecord.ReadSize) & ~7);
            }
        }
    }

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
                            if (UuidShape.IsLockable(v) && known.Has(v))
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
                if (v[i] != 0 && UuidShape.IsLockable(v[i])) locks++;
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

    // Last reading actually published, so an unchanged one is not rewritten.
    // -1 rather than 0, because 0 is a real reading (nothing is locked) and
    // has to be published the first time it is seen.
    static long publishedUuid = -1;
    static string publishedState = "";
    static readonly Stopwatch sincePublish = Stopwatch.StartNew();

    // Publishing is a file write plus a rename. The waiting_for_lock branch
    // ran it on every poll -- ten writes a second at the old rate, forty at
    // the new one -- for a reading that had not changed. The app reacts to
    // uuid and state and treats anything older than 30s as stale, so an
    // unchanged reading only has to be republished often enough to stay
    // fresh, which is what the heartbeat is for. `detail` is deliberately not
    // compared: it carries counters that move constantly and that nothing on
    // the other side acts on.
    // Split out so the decision can be tested without a disk. Getting it
    // wrong in the suppressing direction freezes the overlay on a stale
    // reading, which is exactly the symptom the throttle was added to avoid
    // causing.
    internal static bool ShouldPublish(long uuid, string state,
        long lastUuid, string lastState, long msSincePublish)
    {
        if (uuid != lastUuid || state != lastState) return true;
        return msSincePublish >= HeartbeatMs;
    }

    static void PublishState(int pid, long uuid, string state, string? detail)
    {
        if (!ShouldPublish(uuid, state, publishedUuid, publishedState,
                sincePublish.ElapsedMilliseconds)) return;
        publishedUuid = uuid;
        publishedState = state;
        sincePublish.Restart();
        WriteState(pid, uuid, state, detail);
    }

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
