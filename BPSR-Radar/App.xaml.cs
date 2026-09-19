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
