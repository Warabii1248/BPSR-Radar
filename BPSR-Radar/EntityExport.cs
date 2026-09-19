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

    // A second is fine while the world is steady: the helper is watching a
    // record it already found and barely consults this file.
    private const int SettledMs = 1000;

    // It is not fine right after a field transition. SetScene clears the
    // entity list, and until this file names a monster again the helper can
    // neither re-adopt the record nor scan for one -- it sits in its
    // no_targets gate. A second of publishing lag lands directly in the
    // delay before the first target appears in the new field.
    private const int AfterSceneChangeMs = 250;

    // How long to keep publishing at the faster rate. Long enough to cover
    // the game repopulating the scene; after that the steady rate resumes
    // whether or not anything turned up, so a quiet field costs nothing.
    private const int SceneBurstMs = 10000;

    private static int CurrentDelayMs()
    {
        long changed = RadarTracker.SceneChangedAtMs;
        if (changed == 0) return SettledMs;
        long since = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - changed;
        return since >= 0 && since < SceneBurstMs ? AfterSceneChangeMs : SettledMs;
    }

    private static async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { WriteOnce(); } catch { }
            // Sliced, because the delay is chosen before the sleep starts. A
            // scene change landing inside a one second sleep would otherwise
            // wait out the whole of it -- and the first publish after a
            // transition is the one the faster rate exists for.
            try
            {
                int slept = 0;
                while (slept < CurrentDelayMs() && !ct.IsCancellationRequested)
                {
                    await Task.Delay(AfterSceneChangeMs, ct);
                    slept += AfterSceneChangeMs;
                }
            }
            catch (TaskCanceledException) { break; }
        }
    }

    private static bool lastCaptureRunning;
    private static bool captureNoted;
    private static long lastCaptureNoteMs;
    private static long lastPacketsObserved;
    private const int CaptureHeartbeatMs = 60000;

    private static void NoteCapture(bool running, long packets)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool changed = !captureNoted || running != lastCaptureRunning;
        if (!changed && now - lastCaptureNoteMs < CaptureHeartbeatMs) return;
        // A heartbeat that says "still running, still zero packets" is the
        // one worth having: a capture that is up but deaf is the failure that
        // reads as an empty world.
        TrackerLog.Write($"app capture running={running} packets={packets}" +
                         (captureNoted ? $" (+{packets - lastPacketsObserved})" : ""));
        captureNoted = true;
        lastCaptureRunning = running;
        lastCaptureNoteMs = now;
        lastPacketsObserved = packets;
    }

    private static void WriteOnce()
    {
        var snapshot = RadarTracker.ExportLiveSnapshot();
        var payload = new
        {
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            self = RadarTracker.PlayerUuid,
            // The helper watches one address for the life of the game and has
            // no packet feed of its own, so a zone change is invisible to it.
            // It kept polling a record the new scene had freed, and the only
            // way out was a ten minute timer -- measured 2026-09-19 as three
            // and a half minutes of a dead slot and an overlay stuck at "-".
            scene = snapshot.Scene,
            packetsObserved = PacketDiag.ObservedCount,
            captureRunning = CaptureService.Running,
            entities = snapshot.Entities,
        };
        // entities.json is rewritten every second, so these two have never
        // had a history -- and a capture that stops looks from the helper's
        // side exactly like a world with no monsters in it. Record the
        // transitions, and a heartbeat slow enough to be free.
        NoteCapture(payload.captureRunning, payload.packetsObserved);

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
