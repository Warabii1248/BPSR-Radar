using System.Windows;

namespace BpsrRadar;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Offline harness switches:
        //   --record <dir>        record a whole session: packets to
        //                         <dir>\session.pcap and memory snapshots to
        //                         <dir>\snapshots. One play session recorded
        //                         this way can be replayed offline as often as
        //                         an algorithm change needs.
        //   --record-pcap <path>  packets only
        //   --replay-pcap <path>  read packets from a file instead of a device
        //   --replay-realtime     reproduce original packet timing (default: fast)
        for (int i = 0; i < e.Args.Length; i++)
        {
            switch (e.Args[i])
            {
                case "--record" when i + 1 < e.Args.Length:
                {
                    string dir = e.Args[++i];
                    System.IO.Directory.CreateDirectory(dir);
                    CaptureService.RecordPcapPath = System.IO.Path.Combine(dir, "session.pcap");
                    LockTargetService.SnapshotDir = System.IO.Path.Combine(dir, "snapshots");
                    PacketDiag.DumpPath = System.IO.Path.Combine(dir, "packets.tsv");
                    PacketDiag.DiagLog = true;
                    MarkLog.Enabled = true;
                    MarkLog.Reset();
                    break;
                }
                // Ground-truth run: the player marks each lock with Ctrl+Alt+M
                // (and each clear with Ctrl+Alt+N) and the helper sweeps
                // memory right then. No whole-memory snapshots -- the sweeps
                // are the evidence and cost a few hundred KB instead of a GB.
                case "--record-marks" when i + 1 < e.Args.Length:
                {
                    string markDir = e.Args[++i];
                    System.IO.Directory.CreateDirectory(markDir);
                    CaptureService.RecordPcapPath = System.IO.Path.Combine(markDir, "session.pcap");
                    PacketDiag.DumpPath = System.IO.Path.Combine(markDir, "packets.tsv");
                    PacketDiag.DiagLog = true;
                    LockTargetService.SnapshotDir = System.IO.Path.Combine(markDir, "snapshots");
                    LockTargetService.SnapshotMax = 0;
                    MarkLog.Enabled = true;
                    MarkLog.Reset();
                    break;
                }
                case "--dump-packets" when i + 1 < e.Args.Length:
                    PacketDiag.DumpPath = e.Args[++i];
                    PacketDiag.DiagLog = true;
                    break;
                case "--record-pcap" when i + 1 < e.Args.Length:
                    CaptureService.RecordPcapPath = e.Args[++i];
                    break;
                case "--replay-pcap" when i + 1 < e.Args.Length:
                    CaptureService.ReplayPcapPath = e.Args[++i];
                    break;
                case "--replay-realtime":
                    CaptureService.ReplayRealtime = true;
                    break;
            }
        }

        RadarSettings.Load();
        GameDataTables.Load();
        RadarTracker.LoadNameCache();

        // --check <file>: confirm the packaged build can find its data, then
        // exit. A missing table is silent -- LoadTable returns an empty
        // dictionary and the app starts normally, showing "#1234" instead of
        // every name -- so packaging changes need something that fails loudly.
        // Exists because single-file publishing moves what
        // AppContext.BaseDirectory means, and the tables are read through it.
        if (Array.IndexOf(e.Args, "--check") is int ci and >= 0)
        {
            string report =
                $"monsters={GameDataTables.Monsters.Count} " +
                $"dummies={GameDataTables.Dummys.Count} " +
                $"scenes={GameDataTables.Scenes.Count} " +
                $"base={AppContext.BaseDirectory}";
            bool ok = GameDataTables.Monsters.Count > 0
                && GameDataTables.Dummys.Count > 0
                && GameDataTables.Scenes.Count > 0;
            if (ci + 1 < e.Args.Length)
            {
                try { System.IO.File.WriteAllText(e.Args[ci + 1], (ok ? "OK " : "FAIL ") + report); }
                catch { }
            }
            Shutdown(ok ? 0 : 1);
            return;
        }

        Serilog.Log.Logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Debug()
            .CreateLogger();

        CaptureService.Start();
        EntityExport.Initialize();
        LockTargetService.Initialize();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        EntityExport.Shutdown();
        LockTargetService.Shutdown();
        CaptureService.Stop();
        PacketDiag.Close();
        RadarTracker.SaveNameCache();
        RadarSettings.Save();
        base.OnExit(e);
    }
}
