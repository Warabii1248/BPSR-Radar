using System.Linq;
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
    public const int EntDummy = 11;
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
    // What the player can actually put a manual lock on. Monsters, and only
    // monsters -- but named for the question being asked, because the last
    // time this was called "IsMonster" someone (2026-09-19) widened it on a
    // guess and made things worse. The old name is gone rather than kept as
    // an alias: two identically-implemented predicates with different names
    // are how that mistake was made in the first place.
    //
    // EntDummy (11) was that guess, and it is WRONG. The name suggests the
    // guild hall's training dummies; the entities are nothing of the kind.
    // Of 733 in the name cache, 722 carry the summon bit and the names are
    // skill effects -- lightning strikes, meteors, arrow rain, damage
    // proxies. Admitting them would widen every candidate set, open the
    // entity gate in places where nothing is lockable at all, and let a
    // spell effect be published as the player's target.
    //
    // The guild hall dummies really are EntMonster: 0x460040 "Enemy Training
    // Dummy", 0x4b0040 "Elite Enemy Training Dummy", 0xb30040 "Elite
    // Guardian Dummy", all type 1, all confirmed locked and published on
    // 2026-09-19. Whatever kept them off the overlay, it was never their
    // type -- it was scan cost (see LockRecord.Find's skip stride).
    public static bool IsLockable(ulong v) => IsPlausible(v) && TypeOf(v) == EntMonster;

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

    // How many of them the player could actually lock. The scan only matches
    // a lockable uuid, so a list of nothing but players and NPCs is as
    // useless to it as no list at all -- measured 2026-09-19: known=8/fresh
    // producing cand=0 on every scan, five seconds apart, while standing
    // away from anything attackable.
    //
    // This used to be MonsterCount and used UuidShape.IsMonster, which made
    // the guild hall's training dummies invisible to the gate: 58 entities
    // known, zero of them "monsters", so the helper never scanned at all.
    // The name is part of the fix -- counting one thing and calling it
    // another is what kept the gap out of sight.
    public int LockableCount { get; private set; }

    private void CountLockable()
    {
        int n = 0;
        foreach (var u in uuids) { if (UuidShape.IsLockable(u)) n++; }
        LockableCount = n;
    }
    public bool Fresh => (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - fileTs) < 30000;

    public bool Has(ulong uuid) => uuids.Contains(uuid);

    // Which EEntityTypes the list actually holds, commonest first, as
    // "type:count". The guild hall defect turned on exactly one question --
    // had the packet side seen the training dummies at all, or had it seen
    // them and the helper refused them? -- and the log could not tell those
    // apart, because it only ever printed a total and a monster count.
    // Computed only when something is about to print it.
    public string TypeHistogram()
    {
        var counts = new Dictionary<int, int>();
        foreach (var u in uuids)
        {
            int t = UuidShape.TypeOf(u);
            counts[t] = counts.TryGetValue(t, out int n) ? n + 1 : 1;
        }
        var parts = new List<string>();
        foreach (var kv in counts.OrderByDescending(kv => kv.Value))
        {
            parts.Add($"{kv.Key}:{kv.Value}");
        }
        return string.Join(" ", parts);
    }

    // Names are only kept for offline analysis; the live helper never needs
    // them and paying for the dictionary on every refresh would be waste.
    //
    // The comment said that while the code built the dictionary regardless.
    // It mattered little at one reload per two seconds; the live loop now
    // reloads five times a second while it has no record, so the live path
    // turns it off. Describe() is only reached from the analysis modes and
    // from Scanner's debug dump, never from Watch().
    public bool KeepNames { get; set; } = true;
    private Dictionary<ulong, string> names = new();

    public string Describe(ulong uuid) =>
        names.TryGetValue(uuid, out var n) && n.Length > 0 ? n
        : uuids.Contains(uuid) ? "(unnamed)"
        : "-";

    public ulong SelfUuid { get; private set; }

    // The packet side's current scene. The helper has no other way to learn
    // that the world was rebuilt under the record it is watching.
    public uint SceneId { get; private set; }

    // Test hook: supplies the entity set directly instead of reading a file.
    public void Seed(IEnumerable<ulong> seed)
    {
        uuids = new HashSet<ulong>(seed);
        CountLockable();
        fileTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    // The caller decides how stale the list may be, because that depends on
    // what it is about to do with it. Watching a known record barely consults
    // it; recovering from a field transition cannot start until it refills.
    public void Refresh(int minIntervalMs = 2000)
    {
        if ((DateTime.UtcNow - loadedAt).TotalMilliseconds < minIntervalMs) return;
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
                        if (KeepNames && e.TryGetProperty("Name", out var n))
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
            if (root.TryGetProperty("scene", out var sc) && sc.TryGetUInt32(out uint scv)) SceneId = scv;
            if (root.TryGetProperty("ts", out var ts) && ts.TryGetInt64(out long tsv)) fileTs = tsv;
            if (forceFresh) fileTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            uuids = set;
            CountLockable();
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

    // The two fields the lock record's layout comment used to call
    // "positions (double)". Measured 2026-09-19 across five snapshots and two
    // live samples, they are a pair of float32s -- and reading them as a
    // double is what hid the manual lock record.
    //
    //   snapshot        kind  field B raw           as 2x float32
    //   122736          1     0x0000000042c7078b    99.5147 / 0
    //   122811          1     0xbf80000042c6b4d0    99.3531 / -1
    //   122909          1     0x0000000042c7078b    99.5147 / 0
    //   122945          1     0x0000000042c7078b    99.5147 / 0
    //   123016          0     0x4076356c42c6b4d1    99.3532 / 3.84701
    //   live 20:55:28   1     0x3f021c68430f649a    143.393 / 0.5082
    //   live 20:55:38   1     0x3edb7294430f41c9    143.257 / 0.4286
    //
    // Doubleish reads the top 32 bits as a double's exponent and demands
    // [0x3ff00000, 0x44000000), so it passes only when the SECOND float
    // happens to land between about 1.88 and 512. Every manual record in the
    // corpus -- four of four -- has a second float of 0, -1 or 0.5 and was
    // therefore rejected. The auto record, whose second float was 3.85, was
    // accepted. That is the whole of "a few individuals show -", and it is
    // also why the scan so often reported rec=0 with a lock plainly held.
    //
    // Both readings are accepted rather than replaced, because field A still
    // reads as a plain double in every sample (-32768, 32768, -1321.6) and
    // nothing measured says which the game intends.
    // The three qwords the layout calls "0". Measured twice, identically, in
    // two sessions and at two different record addresses:
    //
    //   21:21:18.636  0x207fd231950  z1=0x100000001 z2=0x100000000 z3=0x100000001
    //   21:43:08.116  0x232e4a8c950  z1=0x100000001 z2=0x100000000 z3=0x100000001
    //
    // Read as pairs of int32 those are (1,1), (0,1), (1,1) -- flags, not
    // padding. A record in that state cannot be found by the scan, which is
    // the same class of mistake as reading the position fields as doubles.
    //
    // Only 0 and 1 are admitted in each half, which keeps nearly all of the
    // selectivity: measured over the eight-snapshot corpus this accepts
    // exactly the records the strict test did (18, unchanged), so the cost of
    // the widening is zero on every sample there is.
    //
    // Deliberately not widened further. One sample said nothing and the code
    // was left alone for it; two identical samples are what changed the
    // answer.
    public static bool ZeroOrFlagPair(ulong v) => (uint)v <= 1 && (uint)(v >> 32) <= 1;

    public static bool PositionField(ulong v) => Doubleish(v) || FloatPairish(v);

    public static bool FloatPairish(ulong v) =>
        Float32ish(unchecked((uint)v)) && Float32ish(unchecked((uint)(v >> 32)));

    // Zero, or a normal float. Nothing more: a magnitude bound was tried and
    // measured to reject nothing the exponent test had not already rejected
    // (18 records either way across the eight-snapshot corpus), so it was an
    // untested condition standing in a hot loop and it is gone.
    //
    // What does the work is the exponent. The corpus contains a heap pointer
    // sitting in one of these fields, 0x0000024bcd0ca720, whose upper half is
    // 0x0000024b -- a denormal -- and that is what keeps this predicate from
    // accepting anything at all.
    private static bool Float32ish(uint bits)
    {
        if (bits == 0) return true;
        uint exp = (bits >> 23) & 0xFF;
        return exp != 0 && exp != 0xFF;
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
