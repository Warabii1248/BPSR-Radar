using System.Buffers.Binary;
using System.Text.Json;

// Lock-list scanning, split out of Program so the exact same code can run
// against a live process or a captured snapshot (see MemSource.cs).

// Entity uuid shape, mirrored from the packet side (RadarTracker):
//   uuid = entityId << 16 | entityType << 6
// and the server sets bit 15 for summoned entities. So the low six bits are
// always zero, bits 6..14 carry EEntityType and bit 15 marks a summon.
static class UuidShape
{
    public const int EntMonster = 1;
    public const int EntCount = 24;

    // Upper bound. Every monster uuid in the recorded name cache sits below
    // 2^38 (largest 0x4000000040) while the game's heaps live at 2^41 and up
    // (0x1c8d........, 0x1ec8........), so this rejects a heap pointer whose
    // low 16 bits happen to read as an entity tag. Player uuids run higher
    // than monsters, but players cannot be locked, so the tight bound is safe.
    public const ulong MaxUuid = 1UL << 40;

    public static int TypeOf(ulong v) => (int)((v >> 6) & 0x1FF);
    public static bool IsSummon(ulong v) => ((v >> 15) & 1) == 1;
    public static long EntityIdOf(ulong v) => (long)(v >> 16);

    public static bool IsPlausible(ulong v)
    {
        if ((v & 0x3F) != 0) return false;
        if (v < 0x10000 || v >= MaxUuid) return false;
        int t = TypeOf(v);
        return t > 0 && t < EntCount;
    }

    // Monsters, including summoned ones. 0x0040 is a plain monster and 0x8040
    // a summon: the resonance/elite bosses ("... - Resonance", "Great Tower
    // Boss") all carry the summon bit, and the old test demanded an exact
    // 0x0040 low word, which is why locking one of them never resolved.
    public static bool IsMonster(ulong v) => IsPlausible(v) && TypeOf(v) == EntMonster;

    public static string Describe(ulong v) =>
        $"0x{v:x} type={TypeOf(v)}{(IsSummon(v) ? "+summon" : "")} id={EntityIdOf(v)}";
}

// The packet-side view of which entities exist, published by the app to
// %TEMP%\BPSR-Radar\entities.json. Used to confirm a candidate uuid without
// another memory sweep: a value the packet stream has never named is almost
// certainly a heap pointer that happens to fit the shape. Confirmation is
// advisory only -- if the app's capture is down the file goes stale and the
// structural test has to stand alone, which must not stop the helper working.
sealed class KnownEntities
{
    private readonly string path;
    private HashSet<ulong> uuids = new();
    private DateTime loadedAt = DateTime.MinValue;
    private long fileTs;

    public KnownEntities(string path) => this.path = path;

    public int Count => uuids.Count;

    // How many of them are monsters. The scan only ever matches a monster
    // uuid, so a list of nothing but players and NPCs is as useless to it as
    // no list at all -- measured 2026-09-19: known=8/fresh producing cand=0
    // on every scan, five seconds apart, while standing away from anything
    // attackable.
    public int MonsterCount { get; private set; }

    private void CountMonsters()
    {
        int n = 0;
        foreach (var u in uuids) { if (UuidShape.IsMonster(u)) n++; }
        MonsterCount = n;
    }
    public bool Fresh => (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - fileTs) < 30000;

    public bool Has(ulong uuid) => uuids.Contains(uuid);

    // Names are only kept for offline analysis; the live helper never needs
    // them and paying for the dictionary on every refresh would be waste.
    private Dictionary<ulong, string> names = new();

    public string Describe(ulong uuid) =>
        names.TryGetValue(uuid, out var n) && n.Length > 0 ? n
        : uuids.Contains(uuid) ? "(unnamed)"
        : "-";

    public ulong SelfUuid { get; private set; }

    // Test hook: supplies the entity set directly instead of reading a file.
    public void Seed(IEnumerable<ulong> seed)
    {
        uuids = new HashSet<ulong>(seed);
        CountMonsters();
        fileTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public void Refresh()
    {
        if ((DateTime.UtcNow - loadedAt).TotalSeconds < 2) return;
        Load(forceFresh: false);
    }

    // forceFresh is for offline replay, where the recorded file is by
    // definition contemporaneous with the snapshot beside it however old the
    // timestamp inside it now looks.
    public void Load(bool forceFresh)
    {
        loadedAt = DateTime.UtcNow;
        try
        {
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var set = new HashSet<ulong>();
            var nameMap = new Dictionary<ulong, string>();
            if (root.TryGetProperty("entities", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    if (e.TryGetProperty("Uuid", out var u) && u.TryGetInt64(out long v) && v != 0)
                    {
                        set.Add(unchecked((ulong)v));
                        if (e.TryGetProperty("Name", out var n))
                        {
                            nameMap[unchecked((ulong)v)] = n.GetString() ?? "";
                        }
                    }
                }
            }
            names = nameMap;
            if (root.TryGetProperty("self", out var s) && s.TryGetInt64(out long sv))
            {
                SelfUuid = unchecked((ulong)sv);
            }
            if (root.TryGetProperty("ts", out var ts) && ts.TryGetInt64(out long tsv)) fileTs = tsv;
            if (forceFresh) fileTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            uuids = set;
            CountMonsters();
        }
        catch
        {
        }
    }
}

sealed class ScanResult
{
    public List<ulong> Elements { get; } = new();
    public List<(ulong b, ulong s)> HitRegions { get; } = new();
    public int RecordCount;
    public int PointerHits;
    public int ElementsConsidered;
    public int Confirmed;
    public string Diag = "";
}

static class Scanner
{
    public const int ChunkSize = 4 * 1024 * 1024;

    // Reads the nine inline slots of an element in one go. slotOk=false means
    // the element itself stopped looking like a lock list, which is the only
    // condition that may retire it. An unexpected uuid is reported as 0 and
    // never treated as element corruption: the previous code retired the
    // element on any uuid shape it did not recognise, so locking a summoned
    // boss destroyed a working element and forced a multi-second rescan.
    public static bool TryReadTarget(IMemSource mem, ulong elem, byte[] scratch,
        out long uuid, out bool slotOk)
    {
        uuid = 0;
        slotOk = true;
        if (!mem.Read(elem + 0x20, scratch, 0x48, out int got) || got < 0x48)
        {
            slotOk = false;
            return false;
        }

        ulong rec = 0;
        for (int s = 0; s < 9; s++)
        {
            ulong p = BinaryPrimitives.ReadUInt64LittleEndian(scratch.AsSpan(s * 8, 8));
            if (p == 0) continue;
            if (!LooksLikeHeapPtr(p)) { slotOk = false; return false; }
            rec = p;
        }
        if (rec == 0) return true; // all slots empty = unlocked

        if (!TryReadU64(mem, rec + 0x10, out ulong u)) { slotOk = false; return false; }
        if (u != 0 && !UuidShape.IsPlausible(u)) return true; // odd value, element still fine
        uuid = (long)u;
        return true;
    }

    public static ScanResult ScanRegions(IMemSource mem, List<(ulong Base, ulong Size)> regions,
        HashSet<ulong> learnedOwners, HashSet<ulong> learnedVtbls, KnownEntities known,
        List<string>? debugDump)
    {
        var result = new ScanResult();

        // Pass 1: candidate target records. With learned owners the record's
        // first qword is matched directly; otherwise the structural signature
        // is used -- including the uuid at +0x10, which the previous version
        // never checked and which is by far the most selective field (it cut
        // the candidate set from ~830k to a few hundred in replay).
        var recordAddrs = new HashSet<ulong>();
        var recLock = new object();
        var hitSet = new HashSet<(ulong, ulong)>();
        ulong[] owners = learnedOwners.Count > 0 ? learnedOwners.ToArray() : Array.Empty<ulong>();
        var ptrBag = new System.Collections.Concurrent.ConcurrentBag<ulong>();

        Parallel.ForEach(regions, region =>
        {
            var (b, s) = region;
            var buf = new byte[ChunkSize];
            var local = new List<ulong>();
            ulong pos = 0;
            while (pos < s)
            {
                int want = (int)Math.Min((ulong)ChunkSize, s - pos);
                if (!mem.Read(b + pos, buf, want, out int read) || read <= 0)
                {
                    pos += (ulong)want;
                    continue;
                }
                if (owners.Length > 0)
                {
                    for (int off = 0; off + 8 <= read; off += 8)
                    {
                        ulong v = U64(buf, off);
                        for (int i = 0; i < owners.Length; i++)
                        {
                            if (v == owners[i]) { local.Add(b + pos + (ulong)off); break; }
                        }
                    }
                }
                else
                {
                    for (int off = 0; off + 0x40 <= read; off += 8)
                    {
                        if (!UuidShape.IsPlausible(U64(buf, off + 0x10))) continue;
                        if (!LooksLikeHeapPtr(U64(buf, off))) continue;
                        if (U64(buf, off + 0x08) != 0 || U64(buf, off + 0x18) != 0 || U64(buf, off + 0x38) != 0) continue;
                        if (!Doubleish(U64(buf, off + 0x20)) || !Doubleish(U64(buf, off + 0x28))) continue;
                        if (!Floatish(U64(buf, off + 0x30))) continue;
                        local.Add(b + pos + (ulong)off);
                    }
                }
                if ((ulong)read >= s - pos) break;
                // Overlap the next chunk by one record: the structural test
                // needs 0x40 bytes, so a record straddling a chunk boundary
                // would otherwise never be seen.
                pos += (ulong)Math.Max(8, read - 0x40);
            }
            if (local.Count > 0)
                lock (recLock) { recordAddrs.UnionWith(local); hitSet.Add(region); }
        });

        // Pass 2: locations pointing at candidate records (element slots).
        Parallel.ForEach(regions, region =>
        {
            var (b, s) = region;
            var buf = new byte[ChunkSize];
            var local = new List<ulong>();
            ulong pos = 0;
            while (pos < s)
            {
                int want = (int)Math.Min((ulong)ChunkSize, s - pos);
                if (!mem.Read(b + pos, buf, want, out int read) || read <= 0)
                {
                    pos += (ulong)want;
                    continue;
                }
                for (int off = 0; off + 8 <= read; off += 8)
                {
                    ulong v = U64(buf, off);
                    if (LooksLikeHeapPtr(v) && recordAddrs.Contains(v))
                        local.Add(b + pos + (ulong)off);
                }
                pos += (ulong)read;
            }
            if (local.Count > 0)
            {
                foreach (var a in local) ptrBag.Add(a);
                lock (recLock) hitSet.Add(region);
            }
        });

        var ptrHits = ptrBag.ToList();
        var seen = new HashSet<ulong>();
        var confirmed = new List<ulong>();
        var unconfirmed = new List<ulong>();
        int elements = 0;

        foreach (var hit in ptrHits)
        {
            for (int slotOff = 0x20; slotOff <= 0x68; slotOff += 8)
            {
                ulong elem = hit - (ulong)slotOff;
                if (!seen.Add(elem)) continue;
                if (!TryReadU64(mem, elem, out ulong vtbl) || !LooksLikeHeapPtr(vtbl)) continue;
                if (learnedVtbls.Count > 0 && !learnedVtbls.Contains(vtbl)) continue;
                if (!TryReadU64(mem, elem + 0x18, out ulong cap) || cap == 0 || cap > 64) continue;

                int live = 0;
                bool ok = true;
                var liveRecs = new List<ulong>(9);
                for (int s = 0; s < 9; s++)
                {
                    if (!TryReadU64(mem, elem + 0x20 + (ulong)(s * 8), out ulong p)) { ok = false; break; }
                    if (p == 0) continue;
                    if (!recordAddrs.Contains(p)) { ok = false; break; }
                    live++; liveRecs.Add(p);
                }
                if (!ok || live == 0) continue;

                // Owner consistency: every record in a real lock list shares
                // the same +0x00 owner. Junk lists point at unrelated objects.
                ulong owner = 0;
                foreach (var rec in liveRecs)
                {
                    if (!TryReadU64(mem, rec, out ulong o) || o == 0 || !LooksLikeHeapPtr(o)) { ok = false; break; }
                    if (owner == 0) owner = o;
                    else if (o != owner) { ok = false; break; }
                }
                if (!ok) continue;
                elements++;

                ulong rec0 = liveRecs[^1];
                TryReadU64(mem, rec0 + 0x10, out ulong uuid);
                TryReadU64(mem, rec0 + 0x20, out ulong d1);
                TryReadU64(mem, rec0 + 0x28, out ulong d2);
                double f1 = BitConverter.Int64BitsToDouble((long)d1);
                double f2 = BitConverter.Int64BitsToDouble((long)d2);
                bool shapeOk = UuidShape.IsPlausible(uuid)
                    && !double.IsNaN(f1) && Math.Abs(f1) < 1e15
                    && !double.IsNaN(f2) && Math.Abs(f2) < 1e15;

                if (debugDump != null)
                {
                    var slots = new System.Text.StringBuilder();
                    foreach (var rec in liveRecs)
                    {
                        TryReadU64(mem, rec + 0x10, out ulong su);
                        slots.Append($" [{UuidShape.Describe(su)} \"{known.Describe(su)}\"" +
                                     (su == known.SelfUuid && su != 0 ? " SELF" : "") + "]");
                    }
                    debugDump.Add($"elem=0x{elem:x} vtbl=0x{vtbl:x} cap={cap} owner=0x{owner:x} " +
                        $"live={live} shapeOk={shapeOk} f1={f1:G6} f2={f2:G6}{slots}");
                }

                if (!shapeOk) continue;

                // Confirmed candidates (the packet stream has named this uuid)
                // are preferred; unconfirmed ones are still kept so the helper
                // keeps working when the app's capture is down.
                if (known.Fresh && known.Has(uuid)) confirmed.Add(elem);
                else unconfirmed.Add(elem);

                // Learn only from confirmed elements. The container class is a
                // generic small-list used all over the game -- 153 of 289
                // rejected elements shared one vtbl -- so learning from a
                // guess can lock the scanner onto junk for the whole session.
                if (known.Fresh && known.Has(uuid))
                {
                    if (learnedOwners.Count < 4) learnedOwners.Add(owner);
                    if (learnedVtbls.Count < 4) learnedVtbls.Add(vtbl);
                }
            }
        }

        result.Elements.AddRange(confirmed);
        result.Elements.AddRange(unconfirmed);
        result.HitRegions.AddRange(hitSet);
        result.RecordCount = recordAddrs.Count;
        result.PointerHits = ptrHits.Count;
        result.ElementsConsidered = elements;
        result.Confirmed = confirmed.Count;
        result.Diag = $"rec={recordAddrs.Count} ptr={ptrHits.Count} el={elements} " +
                      $"pl={result.Elements.Count} ok={confirmed.Count}";
        return result;
    }

    public static ulong U64(byte[] buf, int off) => BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(off, 8));

    [ThreadStatic] private static byte[]? qword;

    public static bool TryReadU64(IMemSource mem, ulong addr, out ulong v)
    {
        v = 0;
        qword ??= new byte[8];
        if (!mem.Read(addr, qword, 8, out int n) || n != 8) return false;
        v = U64(qword, 0);
        return true;
    }

    // Magnitude tests for the record's position fields. The sign bit is
    // masked off: world coordinates are routinely negative, and the original
    // form compared the raw high word, so every record at a negative
    // coordinate was silently skipped.
    public static bool Doubleish(ulong v)
    {
        if (v == 0) return true;
        uint hi = (uint)((v >> 32) & 0x7FFFFFFF);
        return hi >= 0x3ff00000 && hi < 0x44000000;
    }

    public static bool Floatish(ulong v)
    {
        if (v == 0) return true;
        if ((v >> 32) != 0) return false;
        uint lo = (uint)v & 0x7FFFFFFF;
        return lo >= 0x3f000000 && lo < 0x46000000;
    }

    public static bool LooksLikeHeapPtr(ulong v) => v is >= 0x1_0000_0000 and < 0x8000_0000_0000;
}
