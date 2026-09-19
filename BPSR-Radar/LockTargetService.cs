using System.ComponentModel;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace BpsrRadar;

// Reports the player's current manual lock-on target by reading state
// produced by the elevated BPSR-Radar.LockTarget helper process.
// Port of the upstream lock-on target manager.
internal static class LockTargetService
{
    private const string HelperExeName = "BPSR-Radar.LockTarget.exe";
    private const string HelperProcName = "BPSR-Radar.LockTarget";
    private const int StaleMs = 30000;

    private static readonly string StatePath = Path.Combine(Path.GetTempPath(), "BPSR-Radar", "locktarget.json");
    private static readonly string EventLogPath = Path.Combine(Path.GetDirectoryName(StatePath)!, "lock-events.log");

    // Offline harness: when set, the helper is launched with --snapshot-dir so
    // it captures the target's memory at the moments a scan succeeds or fails.
    public static string? SnapshotDir;

    // Whole-memory snapshots cost ~1 GB and a few seconds each. A mark-driven
    // run does not want them: its sweeps are the evidence, and a snapshot in
    // the middle of one would stall the poll loop for no gain. 0 disables.
    public static int SnapshotMax = 8;

    public static long CurrentTargetUuid;
    public static IReadOnlyDictionary<long, ulong>? CurrentTargetAttrs;
    public static int CurrentTargetTypeUid;
    // off | no_game | starting | watching | denied | error
    //
    // Behind a property because the twelve places that set it were a complete
    // record of what the app decided and not one of them wrote a line. On
    // 2026-09-19 the helper died at 20:24:15 and this sat on "error" for the
    // rest of the session; the log, which the app was still appending entity
    // exports to, said nothing about it at all, and the cause had to be found
    // by reading the code instead. A thirteenth assignment cannot now be
    // added without the transition appearing.
    private static string status = "off";

    public static string Status
    {
        get => status;
        set
        {
            if (value == status) return;
            string was = status;
            status = value;
            LogEvent($"app status {was} -> {value}");
        }
    }

    // Packet-derived lock target (WorldActivityNtf ClientTargetChange 0x300B,
    // field 4 TargetUuid). Timestamp precedence vs the memory helper: whichever
    // signal changed most recently wins, so a packet lock displays instantly
    // and the helper covers cases where the packet does not fire.
    private static long packetUuid;
    private static long packetAtMs;
    private static long observedHelperUuid;
    private static long helperUuidAtMs;

    // Raised when the published target changes. The overlay redraws on this
    // instead of waiting for the next UI timer tick, which was the last of
    // the three sampling delays between a lock and the text on screen.
    // Raised from the capture thread as well as from the service loop, so a
    // handler has to marshal to its own thread.
    public static event Action? TargetChanged;

    private static void SetTarget(long uuid, IReadOnlyDictionary<long, ulong>? attrs, int typeUid)
    {
        bool changed = uuid != CurrentTargetUuid;
        long was = CurrentTargetUuid;
        // Extras before the uuid: a reader that sees the new uuid then sees
        // the extras that belong to it, rather than the previous target's.
        CurrentTargetAttrs = attrs;
        CurrentTargetTypeUid = typeUid;
        CurrentTargetUuid = uuid;
        if (!changed) return;
        // The end of the chain. Everything else in this log is something the
        // helper decided; this is what the overlay was actually given, after
        // the packet-versus-helper precedence rule has run. They are expected
        // to agree and there was no record of whether they did.
        LogEvent($"app show 0x{was:x} -> 0x{uuid:x}");
        try { TargetChanged?.Invoke(); } catch { }
    }

    public static void OnPacketTargetUuid(long uuid)
    {
        packetUuid = uuid;
        packetAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // The packet is the earliest signal there is -- it arrives before the
        // helper's next memory poll, let alone before this service next reads
        // the helper's file. Parking it in a field and letting the tick
        // collect it threw that entire lead away. Publishing here agrees with
        // the tick's own precedence rule (whichever signal moved most recently
        // wins) because nothing can have moved more recently than this.
        //
        // The helper's memory-side extras describe the target it last
        // reported, so they are dropped unless they happen to describe this
        // one.
        if (watchedPid != 0 && RadarSettings.Instance.LockTargetEnabled)
        {
            bool sameAsHelper = uuid == observedHelperUuid;
            SetTarget(uuid, sameAsHelper ? CurrentTargetAttrs : null,
                sameAsHelper ? CurrentTargetTypeUid : 0);
        }

        LogEvent($"pkt uuid=0x{uuid:x}");
    }

    // The capture thread used to append to the log file inline, one
    // open/write/close per target change on the thread decoding packets.
    // Queue the line and let the service loop write it in batches.
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> pendingLog = new();
    private static readonly Stopwatch sinceLogFlush = Stopwatch.StartNew();
    private const int LogFlushMs = 1000;

    private static void LogEvent(string line)
    {
        // Bounded: the log is a debugging aid, and an unbounded queue behind
        // a stalled disk would grow without limit.
        if (pendingLog.Count >= 256) return;
        pendingLog.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {line}\n");
    }

    // Lines that were dequeued but could not be written, kept for the next
    // attempt. The helper appends to this same file from another process and
    // File.AppendAllText opens it with FileShare.Read, so a collision throws
    // -- and the old version had already emptied the queue by then, losing up
    // to 256 lines without a trace. Losing diagnostics silently is how three
    // diagnoses of the same defect went wrong.
    private static string carried = "";

    private static void FlushLog(bool force = false)
    {
        if (pendingLog.IsEmpty && carried.Length == 0) return;
        if (!force && sinceLogFlush.ElapsedMilliseconds < LogFlushMs) return;
        sinceLogFlush.Restart();

        var sb = new System.Text.StringBuilder(carried);
        while (pendingLog.TryDequeue(out var line)) sb.Append(line);
        string batch = sb.ToString();
        if (batch.Length == 0) return;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(EventLogPath)!);
                File.AppendAllText(EventLogPath, batch);
                carried = "";
                return;
            }
            catch { }
            try { Thread.Sleep(10); } catch { }
        }
        // Still blocked. Carry it rather than drop it, bounded so a
        // permanently unwritable file cannot grow this without limit.
        carried = batch.Length > 64 * 1024 ? batch[^(64 * 1024)..] : batch;
    }

    private static int watchedPid;
    private static bool elevationDenied;

    // Two guards on the retirement request, both of which the faster tick
    // made necessary.
    //
    // It is called from a branch that runs on every tick, and it enumerates
    // every process and writes a file. At 250ms that was four times a second
    // for at most ten seconds; at 30ms it is thirty-three.
    //
    // Worse, it names every running helper by pid -- including one launched
    // moments earlier. The old helper's state file stays Fresh for thirty
    // seconds after it is replaced, so the misconfigured test goes on passing
    // until the new helper publishes; with a 30ms tick that window is reached
    // long before a cold-starting elevated process writes its first line, and
    // the new helper is told to retire. It does, the launch rate limit has
    // already been cleared, and the next tick launches another -- a UAC
    // prompt loop.
    private static DateTime lastStopRequest = DateTime.MinValue;
    private static DateTime suppressMisconfiguredUntil = DateTime.MinValue;
    private const int StopRequestGapMs = 1000;
    private const int PostLaunchGraceSeconds = 8;
    private static DateTime lastLaunch = DateTime.MinValue;

    // Consecutive relaunches that followed an error state. Reset as soon as a
    // helper publishes a usable reading.
    private static int errorRelaunches;

    // 30s, 60s, 120s, 240s, then every five minutes. The first retry is as
    // prompt as it was for any other missing helper, so a one-off crash still
    // recovers in half a minute.
    private static double ErrorRelaunchCooldownSeconds() =>
        Math.Min(30 * Math.Pow(2, Math.Min(errorRelaunches, 4)), 300);
    private static DateTime staleHelperSince = DateTime.MinValue;
    private static CancellationTokenSource? cts;
    private static Task? worker;

    public static void Initialize()
    {
        cts = new CancellationTokenSource();
        worker = Task.Run(() => Loop(cts.Token));
    }

    public static void Shutdown()
    {
        try { cts?.Cancel(); worker?.Wait(2000); } catch { }
        // Whatever the loop had not written yet, including the last target
        // change before the app was closed.
        try { FlushLog(force: true); } catch { }
    }

    // Shown on the target overlay for a few seconds, over everything else.
    // During a mark run this is the only feedback the player can actually see
    // without leaving the game.
    private static string transientMessage = "";
    private static DateTime transientUntil = DateTime.MinValue;

    public static void Flash(string message, double seconds = 3)
    {
        transientMessage = message;
        transientUntil = DateTime.UtcNow.AddSeconds(seconds);
    }

    // True while the locked target is an elite or a boss. The overlay draws
    // those in bold: they are what the player is picking out of a pull.
    public static bool TargetIsStrong =>
        CurrentTargetUuid != 0
        && RadarCanvas.IsStrong(RadarTracker.ClassOf(CurrentTargetUuid, CurrentTargetTypeUid));

    public static string StatusText
    {
        get
        {
            if (DateTime.UtcNow < transientUntil)
            {
                return transientMessage;
            }
            if (CurrentTargetUuid != 0)
            {
                return RadarTracker.Describe(CurrentTargetUuid, CurrentTargetAttrs, CurrentTargetTypeUid);
            }
            return Status switch
            {
                "starting" or "scanning" or "rescanning" or "waiting" or "restarting helper" => "(scanning)",
                // Nothing is locked. The helper distinguishes "nothing
                // lockable is near" from "something is, but you have not
                // locked it"; to a player those are the same thing and the
                // overlay says so. "no_monsters" is the old spelling of
                // "no_targets", kept so a helper left running from a previous
                // build still reads as "-" rather than "(scanning)".
                "waiting_for_lock" or "watching" or "no_targets" or "no_monsters"
                    or "waiting_for_entities" => "-",
                "no_game" => "",
                "denied" => "(needs admin)",
                _ when Status.StartsWith("error") => "(error)",
                _ when Status.StartsWith("stale helper") => "(stale helper)",
                // Anything unmapped would render blank, which reads as "the
                // feature is broken" rather than "something is in progress".
                _ => "(scanning)",
            };
        }
    }

    // Two cadences, because the tick used to do two very different jobs on
    // one clock. Reading the helper's state file is a couple of hundred bytes
    // and sits on the last hop before the overlay, so it has to be quick.
    // Finding the game and checking the helper is alive enumerates every
    // process on the machine -- far too expensive at that rate, and neither
    // answer changes from one frame to the next. Chaining the cheap job to
    // the expensive one is what held the whole chain at 250ms.
    private const int TickMs = 30;
    private const int ProcessScanMs = 1000;

    private static int cachedGamePid;
    private static bool cachedHelperRunning;
    private static string cachedWantedCfg = "";
    private static readonly Stopwatch sinceProcessScan = Stopwatch.StartNew();
    private static bool processScanned;

    private static void RefreshProcessCache(bool force = false)
    {
        if (!force && processScanned && sinceProcessScan.ElapsedMilliseconds < ProcessScanMs) return;
        processScanned = true;
        sinceProcessScan.Restart();
        cachedGamePid = FindGamePid();
        cachedHelperRunning = HelperRunning();
        // Also slow-moving, and it stats the helper binary. It is read in the
        // steady-state branch, so leaving it uncached would have put a file
        // stat on every tick.
        cachedWantedCfg = WantedCfg();
    }

    private static async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Tick(); }
            catch { }
            try { FlushLog(); }
            catch { }
            try { await Task.Delay(TickMs, ct); } catch (TaskCanceledException) { break; }
        }
    }

    private static void Tick()
    {
        if (!RadarSettings.Instance.LockTargetEnabled)
        {
            SetTarget(0, null, 0);
            Status = "off";
            return;
        }

        RefreshProcessCache();
        int pid = cachedGamePid;
        if (pid == 0)
        {
            SetTarget(0, null, 0);
            Status = "no_game";
            watchedPid = 0;
            lastLaunch = DateTime.MinValue;
            elevationDenied = false;
            return;
        }

        if (pid != watchedPid)
        {
            watchedPid = pid;
            elevationDenied = false;
            lastLaunch = DateTime.MinValue;
            SetTarget(0, null, 0);
            packetUuid = 0;
            packetAtMs = 0;
            observedHelperUuid = 0;
            helperUuidAtMs = 0;
        }

        var state = ReadState();

        // The helper only exits when the game does, so restarting the app
        // with recording switched on leaves the previous, differently
        // configured helper in charge -- and being elevated, it cannot be
        // stopped from here. It watches for this request instead and retires
        // itself, after which the normal launch path starts the right one.
        // A helper predating this mechanism publishes no stamp and ignores the
        // request, so give up after a few seconds and use it rather than
        // waiting forever -- but say so, because its results are from the old
        // configuration.
        bool misconfigured = state != null && state.Pid == pid && state.Fresh && cachedHelperRunning
            && state.Cfg != cachedWantedCfg
            && DateTime.UtcNow >= suppressMisconfiguredUntil;
        if (misconfigured)
        {
            if (staleHelperSince == DateTime.MinValue) staleHelperSince = DateTime.UtcNow;
            if ((DateTime.UtcNow - staleHelperSince).TotalSeconds < 10)
            {
                LogEvent("app asking the running helper to retire (configuration differs)");
                RequestHelperStop();
                Status = "restarting helper";
                SetTarget(0, null, 0);
                return;
            }
        }
        else
        {
            staleHelperSince = DateTime.MinValue;
        }

        // A helper that exited on a stop request leaves its last reading
        // behind, and that file stays "fresh" for another half minute. Reading
        // it would publish a dead helper's target as if it were current, so
        // the process has to still be there.
        if (state != null && state.Pid == pid && state.Fresh && cachedHelperRunning)
        {
            // When it ignored the retirement request, keep saying so: its
            // readings come from the configuration the app no longer wants.
            // A helper that is alive and publishing is the end of any error
            // streak, whatever the last one died of.
            errorRelaunches = 0;
            Status = misconfigured ? "stale helper (close the game to refresh)" : state.State ?? "";
            if (state.Uuid != observedHelperUuid)
            {
                observedHelperUuid = state.Uuid;
                helperUuidAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                PacketDiag.WatchUuid(state.Uuid);
            }
            // Whichever signal moved most recently wins. The packet branch
            // has usually published its own uuid already; this re-states the
            // same decision for the case where the helper is the newer one.
            bool packetWins = packetAtMs >= helperUuidAtMs;
            SetTarget(packetWins ? packetUuid : state.Uuid,
                packetWins ? null : state.Attrs,
                packetWins ? 0 : state.TypeUid);
            return;
        }

        SetTarget(packetUuid, null, 0);

        if (elevationDenied)
        {
            Status = "denied";
            return;
        }
        // An error the helper is still running with is the helper's answer:
        // OpenProcess was refused, the game is protected, and relaunching
        // would change nothing. An error left behind by a helper that then
        // exited is a corpse, and this branch used to treat the two the same
        // and return -- ahead of the relaunch below. Measured 2026-09-19: the
        // helper died at 20:24:15 of an unhandled exception, its state file
        // kept saying "error", and the overlay read "(error)" for the rest of
        // the session. The process was gone the whole time; nothing ever
        // tried to start another one.
        bool helperErrored = state != null && state.Pid == pid
                             && (state.State?.StartsWith("error") ?? false);
        if (helperErrored && cachedHelperRunning)
        {
            Status = state!.State!;
            return;
        }

        if (cachedHelperRunning)
        {
            Status = "starting";
            return;
        }

        // Relaunching a helper that exited with an error is right -- it is
        // how the feature comes back from a crash -- but only up to a point.
        // A helper that cannot open the game at all (OpenProcess refused)
        // writes the error and exits every time, and at a flat thirty seconds
        // that is a UAC prompt every thirty seconds, forever. Before this
        // work the app never relaunched after an error, so the cost of
        // getting the new behaviour wrong is a new kind of nuisance rather
        // than the old kind of silence.
        double cooldown = helperErrored ? ErrorRelaunchCooldownSeconds() : 30;
        if ((DateTime.UtcNow - lastLaunch).TotalSeconds >= cooldown)
        {
            lastLaunch = DateTime.UtcNow;
            if (helperErrored) errorRelaunches++;
            Status = "starting";
            LogEvent($"app launching helper for game pid={pid}" +
                     (helperErrored ? $" after error (attempt {errorRelaunches}, next in {cooldown:F0}s)" : ""));
            LaunchHelper(pid);
            // The cache would otherwise keep saying the helper is absent for
            // up to a second and this branch reads as "never launched".
            RefreshProcessCache(force: true);
            // And give the new one time to publish before the stale state
            // file left by its predecessor can convict it of being stale
            // itself. See suppressMisconfiguredUntil.
            suppressMisconfiguredUntil = DateTime.UtcNow.AddSeconds(PostLaunchGraceSeconds);
        }
        else if (Status != "starting")
        {
            // Still inside the relaunch cooldown. Keep showing the error that
            // is about to be retried rather than a "waiting" that reads as
            // normal progress.
            Status = helperErrored ? state!.State! : "waiting";
        }
    }

    private static int FindGamePid()
    {
        foreach (var name in RadarSettings.Instance.GameExeNames)
        {
            try
            {
                var procs = Process.GetProcessesByName(name);
                if (procs.Length > 0) return procs[0].Id;
            }
            catch { }
        }
        return 0;
    }

    private static bool HelperRunning()
    {
        try { return Process.GetProcessesByName(HelperProcName).Length > 0; }
        catch { return false; }
    }

    // Must match the stamp the helper publishes: snapshot dir, snapshot cap,
    // and the binary's build time so a redeploy also forces a restart.
    private static string WantedCfg()
    {
        long build = 0;
        try
        {
            build = File.GetLastWriteTimeUtc(
                Path.Combine(AppContext.BaseDirectory, HelperExeName)).Ticks;
        }
        catch { }
        return $"{SnapshotDir ?? ""}|{SnapshotMax}|{build}|{RadarSettings.Instance.LockKeyVk}";
    }

    private static void RequestHelperStop()
    {
        if ((DateTime.UtcNow - lastStopRequest).TotalMilliseconds < StopRequestGapMs) return;
        lastStopRequest = DateTime.UtcNow;
        try
        {
            // Every pid in one file. Writing them one after another left only
            // the last one named, so a second helper was never asked at all.
            var pids = Process.GetProcessesByName(HelperProcName).Select(p => p.Id).ToArray();
            if (pids.Length == 0) return;
            File.WriteAllText(
                Path.Combine(Path.GetDirectoryName(StatePath)!, "helper-stop"),
                string.Join(",", pids));
            // The replacement should start the moment the old one is gone,
            // not after the launch rate limit happens to expire.
            lastLaunch = DateTime.MinValue;
        }
        catch { }
    }

    private static void LaunchHelper(int pid)
    {
        string helperPath = Path.Combine(AppContext.BaseDirectory, HelperExeName);
        if (!File.Exists(helperPath))
        {
            Status = "error: helper missing";
            return;
        }
        try
        {
            string args = pid.ToString();
            if (!string.IsNullOrEmpty(SnapshotDir))
            {
                args += $" --snapshot-dir \"{SnapshotDir}\" --max-snapshots {SnapshotMax}";
            }
            args += $" --lock-key {RadarSettings.Instance.LockKeyVk}";
            var psi = new ProcessStartInfo(helperPath, args)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // cancelled UAC
        {
            elevationDenied = true;
            LogEvent("app elevation refused by the user");
        }
        catch (Exception ex)
        {
            Status = "error: " + ex.Message;
        }
    }

    // The helper publishes by writing a temp file and renaming it over this
    // one, so a read can lose the race with that rename and come back empty.
    // At the old tick rate that was rare enough to ignore; at this one it
    // would be frequent, and falling through to "no state" for a single tick
    // drops the target to the packet value, fires a change event and makes
    // the status read as though the helper had died -- a visible flicker.
    //
    // A failed read is not news. Keep the last good reading and let its own
    // timestamp decide when it has gone stale, which is what Fresh is for.
    private static HelperState? lastGoodState;

    private static HelperState? ReadState()
    {
        HelperState? s = null;
        try
        {
            if (File.Exists(StatePath))
            {
                s = JsonSerializer.Deserialize<HelperState>(File.ReadAllText(StatePath));
            }
        }
        catch { }

        if (s != null) lastGoodState = s;
        else s = lastGoodState;
        if (s == null) return null;

        s.Fresh = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - s.Ts) < StaleMs;
        return s;
    }

    private sealed class HelperState
    {
        [System.Text.Json.Serialization.JsonPropertyName("pid")]
        public int Pid { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("uuid")]
        public long Uuid { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("state")]
        public string? State { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("attrs")]
        public Dictionary<long, ulong>? Attrs { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("typeuid")]
        public int TypeUid { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("cfg")]
        public string? Cfg { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("ts")]
        public long Ts { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public bool Fresh { get; set; }
    }
}
