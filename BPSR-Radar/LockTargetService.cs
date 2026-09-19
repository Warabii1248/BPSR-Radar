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
    public static string Status = "off"; // off | no_game | starting | watching | denied | error

    // Packet-derived lock target (WorldActivityNtf ClientTargetChange 0x300B,
    // field 4 TargetUuid). Timestamp precedence vs the memory helper: whichever
    // signal changed most recently wins, so a packet lock displays instantly
    // and the helper covers cases where the packet does not fire.
    private static long packetUuid;
    private static long packetAtMs;
    private static long observedHelperUuid;
    private static long helperUuidAtMs;

    public static void OnPacketTargetUuid(long uuid)
    {
        packetUuid = uuid;
        packetAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try { File.AppendAllText(EventLogPath, $"{DateTime.Now:HH:mm:ss.fff} pkt uuid=0x{uuid:x}\n"); } catch { }
    }

    private static int watchedPid;
    private static bool elevationDenied;
    private static DateTime lastLaunch = DateTime.MinValue;
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
                // Nothing is locked. The helper distinguishes "no monster is
                // near" from "one is, but you have not locked it"; to a
                // player those are the same thing and the overlay says so.
                "waiting_for_lock" or "watching" or "no_monsters" or "waiting_for_entities" => "-",
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

    private static async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Tick(); }
            catch { }
            try { await Task.Delay(250, ct); } catch (TaskCanceledException) { break; }
        }
    }

    private static void Tick()
    {
        if (!RadarSettings.Instance.LockTargetEnabled)
        {
            CurrentTargetUuid = 0;
            Status = "off";
            return;
        }

        int pid = FindGamePid();
        if (pid == 0)
        {
            CurrentTargetUuid = 0;
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
            CurrentTargetUuid = 0;
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
        bool misconfigured = state != null && state.Pid == pid && state.Fresh && HelperRunning()
            && state.Cfg != WantedCfg();
        if (misconfigured)
        {
            if (staleHelperSince == DateTime.MinValue) staleHelperSince = DateTime.UtcNow;
            if ((DateTime.UtcNow - staleHelperSince).TotalSeconds < 10)
            {
                RequestHelperStop();
                Status = "restarting helper";
                CurrentTargetUuid = 0;
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
        if (state != null && state.Pid == pid && state.Fresh && HelperRunning())
        {
            // When it ignored the retirement request, keep saying so: its
            // readings come from the configuration the app no longer wants.
            Status = misconfigured ? "stale helper (close the game to refresh)" : state.State ?? "";
            if (state.Uuid != observedHelperUuid)
            {
                observedHelperUuid = state.Uuid;
                helperUuidAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                PacketDiag.WatchUuid(state.Uuid);
            }
            CurrentTargetUuid = packetAtMs >= helperUuidAtMs ? packetUuid : state.Uuid;
            CurrentTargetAttrs = state.Attrs;
            CurrentTargetTypeUid = state.TypeUid;
            return;
        }

        CurrentTargetUuid = packetUuid;
        CurrentTargetAttrs = null;
        CurrentTargetTypeUid = 0;

        if (elevationDenied)
        {
            Status = "denied";
            return;
        }
        if (state != null && state.Pid == pid && (state.State?.StartsWith("error") ?? false))
        {
            Status = state.State!;
            return;
        }

        if (HelperRunning())
        {
            Status = "starting";
            return;
        }

        if ((DateTime.UtcNow - lastLaunch).TotalSeconds >= 30)
        {
            lastLaunch = DateTime.UtcNow;
            Status = "starting";
            LaunchHelper(pid);
        }
        else if (Status != "starting")
        {
            Status = "waiting";
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
        }
        catch (Exception ex)
        {
            Status = "error: " + ex.Message;
        }
    }

    private static HelperState? ReadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            var s = JsonSerializer.Deserialize<HelperState>(File.ReadAllText(StatePath));
            if (s == null) return null;
            s.Fresh = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - s.Ts) < StaleMs;
            return s;
        }
        catch { return null; }
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
