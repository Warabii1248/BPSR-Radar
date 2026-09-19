using System.IO;
using System.Text.Json;

namespace BpsrRadar;

// Ground-truth marks for the offline harness.
//
// The lock-on state never leaves the client, so no packet says when the
// player locked something and nothing in the capture can label a moment.
// The player presses a hotkey the instant they lock (or clear) a target and
// that timestamp becomes the label: the elevated helper watches this file and
// sweeps memory as soon as a mark lands, so the sweeps either side of a mark
// bracket exactly one lock change.
//
// A line per mark, appended, so a crash still leaves every earlier mark
// intact:  {"ts":<unix ms>,"kind":"lock"|"clear","n":<sequence>}
internal static class MarkLog
{
    private static readonly string Path_ = Path.Combine(
        Path.GetTempPath(), "BPSR-Radar", "marks.jsonl");

    private static readonly object gate = new();
    private static int sequence;

    public static string FilePath => Path_;
    public static int Count => sequence;

    // Set while recording so the hotkeys are only claimed when they are
    // wanted; a released build must not take Ctrl+Alt+M off the player.
    public static bool Enabled;

    public static event Action<string, int>? Marked;

    private static DateTime lastMarkAt = DateTime.MinValue;

    public static void Mark(string kind)
    {
        if (!Enabled) return;

        // M and N are neighbours and one run produced a lock and a clear nine
        // milliseconds apart, which no hand does on purpose.
        if ((DateTime.UtcNow - lastMarkAt).TotalMilliseconds < 500)
        {
            LockTargetService.Flash("mark ignored (double key)");
            return;
        }

        // Without monsters in range there is nothing for the sweep to find,
        // and the player cannot tell from inside the game. Refuse, loudly.
        int monsters = RadarTracker.LiveMonsterCount();
        if (monsters == 0)
        {
            LockTargetService.Flash("NO MONSTERS IN RANGE - move somewhere with enemies", 5);
            return;
        }

        lastMarkAt = DateTime.UtcNow;
        int n;
        lock (gate)
        {
            n = ++sequence;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path_)!);
                File.AppendAllText(Path_, JsonSerializer.Serialize(new
                {
                    ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    kind,
                    n,
                    target = LockTargetService.CurrentTargetUuid,
                }) + "\n");
            }
            catch
            {
            }
        }
        LockTargetService.Flash($"mark {n}: {kind} ({monsters} monsters)");
        Marked?.Invoke(kind, n);
    }

    // Starting a recording must not inherit marks from an earlier run, whose
    // timestamps would pair with memory sweeps that no longer exist.
    public static void Reset()
    {
        lock (gate)
        {
            sequence = 0;
            try { if (File.Exists(Path_)) File.Delete(Path_); } catch { }
        }
    }
}
