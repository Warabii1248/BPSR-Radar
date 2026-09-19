using System.Buffers.Binary;
using System.Runtime.InteropServices;

// End-to-end scanner test that needs no game.
//
// Plants a synthetic lock list in this process's own memory using the layout
// observed in the client, captures a snapshot of it, and runs the real
// Scanner over that snapshot. This exercises pass 1, pass 2, the owner
// consistency check and the uuid shape test on the same code path a live scan
// takes, so an algorithm change can be regression tested in seconds instead
// of a dungeon run.
//
// Case B is the negative control for the summoned-boss defect: entity uuids
// carry the summon flag in bit 15, so a resonance/elite boss reads as 0x8040
// rather than 0x0040. The old predicate demanded (uuid & 0xffff) == 0x40 and
// so rejected every one of them; the test asserts both that the scanner finds
// it now and that the old predicate would have refused it.
static class SelfTestScan
{
    private const int BufSize = 8 * 1024 * 1024;

    // Plain monster, and a summoned (resonance/elite) monster.
    private const ulong UuidTrash = 0x110040;
    private const ulong UuidBoss = 0x1a9b8040;

    private static bool LegacyAccepts(ulong v) =>
        v > 0x1000 && v < 0x1_0000_0000_0000 && (v & 0xffff) == 0x40;

    public static int Run()
    {
        var buf = new byte[BufSize];
        var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
        int failures = 0;
        try
        {
            ulong baseAddr = (ulong)pin.AddrOfPinnedObject().ToInt64();
            int align = (int)((0x40 - (baseAddr & 0x3F)) & 0x3F);

            // Values only have to look like heap pointers; nothing dereferences
            // them except the scanner's own range test.
            ulong ownerA = baseAddr + 0x100000;
            ulong ownerB = baseAddr + 0x200000;
            ulong vtbl = baseAddr + 0x300000;

            // Two independent lock lists, each with two records; the last
            // occupied slot carries the current target.
            int recA0 = align + 0x1000, recA1 = align + 0x1040;
            int recB0 = align + 0x2000, recB1 = align + 0x2040;
            int elemA = align + 0x3000, elemB = align + 0x4000;

            WriteRecord(buf, recA0, ownerA, 0x120040);
            WriteRecord(buf, recA1, ownerA, UuidTrash);
            WriteRecord(buf, recB0, ownerB, 0x130040);
            WriteRecord(buf, recB1, ownerB, UuidBoss);

            WriteElement(buf, elemA, vtbl, cap: 8,
                new[] { baseAddr + (ulong)recA0, baseAddr + (ulong)recA1 });
            WriteElement(buf, elemB, vtbl, cap: 8,
                new[] { baseAddr + (ulong)recB0, baseAddr + (ulong)recB1 });

            Console.WriteLine($"selftest-scan: planted at 0x{baseAddr:x} " +
                              $"elemA=0x{baseAddr + (ulong)elemA:x} elemB=0x{baseAddr + (ulong)elemB:x}");

            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_VM_READ,
                false, Environment.ProcessId);
            if (h == IntPtr.Zero)
            {
                Console.Error.WriteLine($"selftest-scan: OpenProcess(self) failed {Marshal.GetLastWin32Error()}");
                return 1;
            }

            string file = Path.Combine(Path.GetTempPath(), "BPSR-Radar", "selftest-scan.bin");
            long bytes = MemSnapshot.Write(new LiveMem(h), file, s => Console.WriteLine("  " + s));
            Native.CloseHandle(h);
            if (bytes < 0) { Console.Error.WriteLine("selftest-scan: capture refused"); return 1; }

            using var mem = new SnapshotMem(file);
            var known = new KnownEntities("");
            known.Seed(new[] { UuidTrash, UuidBoss, 0x120040UL, 0x130040UL });

            var owners = new HashSet<ulong>();
            var vtbls = new HashSet<ulong>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = Scanner.ScanRegions(mem, mem.Regions(true), owners, vtbls, known, null);
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
            found = found.Distinct().ToList();
            Console.WriteLine($"  {res.Diag} ms={sw.ElapsedMilliseconds} found=[{string.Join(", ", found.Select(f => "0x" + f.ToString("x")))}]");

            failures += Check("plain monster 0x110040 found", found.Contains(UuidTrash));
            failures += Check("summoned boss 0x1a9b8040 found", found.Contains(UuidBoss));
            failures += Check("no heap-pointer false positives", found.All(known.Has));

            // Negative control: the pre-fix predicate must refuse the boss,
            // otherwise this test could pass against the broken build too.
            failures += Check("legacy predicate accepts plain monster", LegacyAccepts(UuidTrash));
            failures += Check("legacy predicate REJECTS summoned boss (control)", !LegacyAccepts(UuidBoss));

            try { File.Delete(file); } catch { }
            Console.WriteLine(failures == 0 ? "selftest-scan: PASS" : $"selftest-scan: FAIL ({failures})");
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            pin.Free();
        }
    }

    private static int Check(string label, bool ok)
    {
        Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {label}");
        return ok ? 0 : 1;
    }

    private static void WriteRecord(byte[] buf, int off, ulong owner, ulong uuid)
    {
        var s = buf.AsSpan();
        BinaryPrimitives.WriteUInt64LittleEndian(s[(off + 0x00)..], owner);
        BinaryPrimitives.WriteUInt64LittleEndian(s[(off + 0x08)..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(s[(off + 0x10)..], uuid);
        BinaryPrimitives.WriteUInt64LittleEndian(s[(off + 0x18)..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(s[(off + 0x20)..], (ulong)BitConverter.DoubleToInt64Bits(1234.5));
        BinaryPrimitives.WriteUInt64LittleEndian(s[(off + 0x28)..], (ulong)BitConverter.DoubleToInt64Bits(-987.25));
        BinaryPrimitives.WriteUInt64LittleEndian(s[(off + 0x30)..], BitConverter.SingleToUInt32Bits(1.5f));
        BinaryPrimitives.WriteUInt64LittleEndian(s[(off + 0x38)..], 0);
    }

    private static void WriteElement(byte[] buf, int off, ulong vtbl, ulong cap, ulong[] records)
    {
        var s = buf.AsSpan();
        BinaryPrimitives.WriteUInt64LittleEndian(s[(off + 0x00)..], vtbl);
        BinaryPrimitives.WriteUInt64LittleEndian(s[(off + 0x18)..], cap);
        for (int i = 0; i < 9; i++)
        {
            ulong v = i < records.Length ? records[i] : 0;
            BinaryPrimitives.WriteUInt64LittleEndian(s[(off + 0x20 + i * 8)..], v);
        }
    }
}
