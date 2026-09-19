using System.IO;

namespace BpsrRadar;

// The packet side's own entries in the helper's event log.
//
// Two investigations in a row stalled on the same thing: the interesting
// failure was a period in which nothing was written, so afterwards there was
// no way to tell which of several states the app had been in. The guild hall
// case needed exactly one fact that nobody was recording -- whether the AOI
// batch carrying an entity arrived before or after the scene change that
// clears the entity list -- and answering it meant asking the player to go
// and reproduce it by hand.
//
// These lines are low frequency by construction: a scene change is rare, and
// the composition line is throttled and only written when it actually moves.
// They share lock-events.log with the helper on purpose, because their
// ordering relative to its scans and uuid transitions is the whole point.
internal static class TrackerLog
{
    private static readonly string Path_ = Path.Combine(
        System.IO.Path.GetTempPath(), "BPSR-Radar", "lock-events.log");

    private static readonly object gate = new();
    private static DateTime lastComposition = DateTime.MinValue;
    private static string lastCompositionText = "";

    public static void Write(string line)
    {
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
                File.AppendAllText(Path_, $"{DateTime.Now:HH:mm:ss.fff} app {line}\n");
            }
        }
        catch { }
    }

    // Written at most once every few seconds, and only when the summary
    // changed. Standing still in a town must not fill the log.
    public static void Composition(string text)
    {
        lock (gate)
        {
            if (text == lastCompositionText) return;
            if ((DateTime.UtcNow - lastComposition).TotalSeconds < 5) return;
            lastComposition = DateTime.UtcNow;
            lastCompositionText = text;
        }
        Write(text);
    }
}
