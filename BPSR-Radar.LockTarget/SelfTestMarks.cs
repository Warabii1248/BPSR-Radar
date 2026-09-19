using System.IO.Compression;

// Verifies the mark differential before it costs anyone play time.
//
// Builds synthetic sweeps matching the real experiment -- lock, lock, clear,
// lock -- and plants the address kinds an actual session contains:
//
//   TRUE     a different monster at each lock              the lock field
//   MISSING  no monster at one of the lock marks           incomplete
//   NODE     the same monster every time                   a registry node
//   PATTERN  a monster-shaped constant nothing names       a bit pattern
//   NOISE    present in one sweep only
//
// Only TRUE may survive.
//
// Note on clear marks: the field measured in the real session keeps the last
// locked uuid after the player clears the lock, so absence at a clear cannot
// be required. An earlier version required it and rejected the right answer.
// Clear marks are recorded and printed, but only lock marks decide. What
// separates the lock from the attack target is the protocol instead -- the
// player does not attack during a mark run.
static class SelfTestMarks
{
    private const ulong TrueField = 0x1111_0000;
    private const ulong MissingField = 0x2222_0000;
    private const ulong NodeField = 0x3333_0000;
    private const ulong NoiseField = 0x4444_0000;
    // One real sweep held 0x4000000040 seventy thousand times. Unfiltered it
    // agrees with every lock mark and buries the field being hunted.
    private const ulong PatternField = 0x5555_0000;
    private const ulong Pattern = 0x4000000040;

    // Planted lock-kind flag, and a decoy that changes for its own reasons.
    private const int FlagOffset = 0x18;
    private const int DecoyOffset = 0x20;
    private const byte ManualFlag = 0x02;
    private const byte AutoFlag = 0x01;

    private const ulong A = 0x140040;   // monster
    private const ulong B = 0x1a0040;   // monster
    private const ulong C = 0x2f8040;   // monster, summoned

    public static int Run()
    {
        string dir = Path.Combine(Path.GetTempPath(), "BPSR-Radar", "selftest-marks");
        try
        {
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.GetFiles(dir, "mark-*.tsv.gz")) File.Delete(f);
            }
            Directory.CreateDirectory(dir);

            Write(dir, 1, "lock", 1000,
                new[] { (TrueField, A), (MissingField, A), (NodeField, A), (NoiseField, B) },
                new[] { (PatternField, Pattern) });
            Write(dir, 2, "lock", 2000,
                new[] { (TrueField, B), (NodeField, A) },
                new[] { (PatternField, Pattern) });
            // The lock field still reads B here, as the real one does.
            Write(dir, 3, "clear", 3000,
                new[] { (TrueField, B), (NodeField, A) },
                new[] { (PatternField, Pattern) });
            Write(dir, 4, "lock", 4000,
                new[] { (TrueField, C), (MissingField, C), (NodeField, A) },
                new[] { (PatternField, Pattern) });
            // Automatic provisional locks: same slot, same kind of value,
            // different state.
            Write(dir, 5, "auto", 5000,
                new[] { (TrueField, A), (NodeField, A) },
                new[] { (PatternField, Pattern) });
            Write(dir, 6, "auto", 6000,
                new[] { (TrueField, B), (NodeField, A) },
                new[] { (PatternField, Pattern) });

            // The ingestion path, byte for byte as the app writes it. The
            // first mark run produced eight marks and zero sweeps because
            // these lines deserialised with n=0, and the differential test
            // below could not see it: it writes sweep files directly.
            string marksFile = Path.Combine(dir, "marks.jsonl");
            File.WriteAllText(marksFile,
                "{\"ts\":1789794703722,\"kind\":\"lock\",\"n\":1,\"target\":10617152}\n" +
                "{\"ts\":1789794772321,\"kind\":\"clear\",\"n\":2,\"target\":0}\n");
            var read = MarkScan.ReadNew(marksFile, 0);
            int failures = 0;
            failures += Check("marks.jsonl parses to 2 marks", read.Count == 2);
            failures += Check("sequence numbers survive", read.Count == 2 && read[0].N == 1 && read[1].N == 2);
            failures += Check("kinds survive", read.Count == 2 && read[0].Kind == "lock" && read[1].Kind == "clear");
            failures += Check("timestamps survive", read.Count == 2 && read[0].Ts == 1789794703722);
            failures += Check("afterSeq filters already-seen marks", MarkScan.ReadNew(marksFile, 1).Count == 1);
            try { File.Delete(marksFile); } catch { }

            var sw = new StringWriter();
            var prev = Console.Out;
            Console.SetOut(sw);
            int rc = Program.DiffMarks(dir);
            Console.SetOut(prev);
            string output = sw.ToString();
            Console.Write(output);

            failures += Check("diff ran", rc == 0);
            failures += Check("lock field found", output.Contains($"0x{TrueField:x}"));
            failures += Check("field keeping its value through a clear is NOT rejected",
                output.Contains($"0x{TrueField:x}"));
            failures += Check("address missing at a lock mark rejected",
                !output.Contains($"0x{MissingField:x}"));
            failures += Check("registry node rejected (never changes)",
                !output.Contains($"0x{NodeField:x}"));
            failures += Check("single-sweep noise rejected", !output.Contains($"0x{NoiseField:x}"));
            failures += Check("monster-shaped constant rejected (not packet-confirmed)",
                !output.Contains($"0x{PatternField:x}"));
            failures += Check("lock-kind flag located at its planted offset",
                output.Contains($"flag at +0x{FlagOffset:x2}: manual=0x{ManualFlag:x2} auto=0x{AutoFlag:x2}"));
            failures += Check("decoy byte not reported as the flag",
                !output.Contains($"flag at +0x{DecoyOffset:x2}"));

            Console.WriteLine(failures == 0 ? "selftest-marks: PASS" : $"selftest-marks: FAIL ({failures})");
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            try
            {
                foreach (var f in Directory.GetFiles(dir, "mark-*.tsv.gz")) File.Delete(f);
            }
            catch { }
        }
    }

    private static int Check(string label, bool ok)
    {
        Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {label}");
        return ok ? 0 : 1;
    }

    private static void Write(string dir, int n, string kind, long ts, (ulong Addr, ulong Uuid)[] hits,
        (ulong Addr, ulong Uuid)[]? unconfirmed = null)
    {
        string path = Path.Combine(dir, $"mark-{n:D3}-{kind}-{ts}.tsv.gz");
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        using var w = new StreamWriter(gz);
        w.WriteLine($"# mark n={n} kind={kind} ts={ts} helperTarget=0x0");
        w.WriteLine("# entities=3 self=0x690ea0280 nodes=3");
        foreach (var (a, u) in hits) w.WriteLine($"V\t{a:x}\t{u:x}\t1");
        if (unconfirmed != null)
        {
            foreach (var (a, u) in unconfirmed) w.WriteLine($"V\t{a:x}\t{u:x}\t0");
        }

        // A window around the lock field: one byte encodes the lock kind, a
        // second changes for unrelated reasons and must not be mistaken for it.
        var window = new byte[MarkScan.WindowSize];
        window[MarkScan.WindowBefore + FlagOffset] = kind == "auto" ? AutoFlag : ManualFlag;
        window[MarkScan.WindowBefore + DecoyOffset] = (byte)(n * 7);
        w.WriteLine($"W\t{TrueField:x}\t{Convert.ToHexString(window)}");
    }
}
