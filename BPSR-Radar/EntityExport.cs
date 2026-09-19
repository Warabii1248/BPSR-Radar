using System.IO;
using System.Text.Json;

namespace BpsrRadar;

// Offline harness: publishes the packet-side view of live entities so the
// elevated memory helper can snapshot it alongside a memory dump. The helper
// cannot see the packet stream, so this file is the only bridge.
internal static class EntityExport
{
    private static readonly string OutPath = Path.Combine(
        Path.GetTempPath(), "BPSR-Radar", "entities.json");

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

    private static async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { WriteOnce(); } catch { }
            try { await Task.Delay(1000, ct); } catch (TaskCanceledException) { break; }
        }
    }

    private static void WriteOnce()
    {
        var payload = new
        {
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            self = RadarTracker.PlayerUuid,
            packetsObserved = PacketDiag.ObservedCount,
            captureRunning = CaptureService.Running,
            entities = RadarTracker.ExportLiveEntities(),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(OutPath)!);
        string tmp = OutPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(payload));
        File.Move(tmp, OutPath, true);

        // Self-report for the offline harness. A correlation log with no hits
        // and a packetsObserved of zero are completely different failures:
        // the first means no packet carries the lock, the second means the
        // capture never ran. Recording this leaves no room to confuse them.
        PacketDiag.Flush();
    }
}
