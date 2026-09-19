using System.Buffers.Binary;

// The lock-on target record, as measured on 2026-09-19 from a run where the
// player marked each state by hand.
//
// Layout, relative to the address holding the uuid:
//
//   -0x08  owner pointer, the same for the record's whole life
//   +0x00  target uuid, absent when nothing is locked
//   +0x08  0, or a pair of int32 flags  (see below)
//   +0x10  0, or a pair of int32 flags  (see below)
//   +0x18  a pair of float32s, or a double  (see below)
//   +0x20  a pair of float32s               (see below)
//   +0x28  0, or a pair of int32 flags  (see below)
//   +0x30  1 = manual lock (the red marker), 0 = automatic provisional lock
//
// +0x18 and +0x20 were called "target position (double)" until 2026-09-19.
// That was wrong for +0x20 and it cost a week: read as a double its value is
// a denormal (3.45e-05, 6.54e-06), read as two float32s it is a coordinate
// and a small scalar (143.393 / 0.5082). Every manual-lock record in the
// snapshot corpus -- four of four -- failed a double test on that field and
// so could not be found by the scan, and the poll, which re-ran the same
// test, read the record as empty. See Scanner.PositionField for the samples.
//
// The game holds one target between the two lock kinds. The automatic lock
// latches onto whatever comes into attack range, so the uuid alone cannot
// drive a display: it would wander with whatever walked past. +0x30 is what
// makes the feature possible.
//
// Measured selectivity: across eight sweeps of a live session, exactly one
// address matched this signature while a lock was held, and none matched
// while nothing was locked.
// How much of the target a scan reads, on the two axes that matter.
//
// `HeapSegmentsOnly` is about which regions: a guess about the game's
// allocator that is right most of the time and measurably wrong some of it
// (see Scanner.HeapSegmentSize).
//
// `ResidentPagesOnly` is about which pages, and it is not a guess about the
// record at all -- it is the difference between a scan the player pays for
// and one they do not. Reading a page that is not in the game's working set
// puts it there and it stays: one measured scan took the game from 5,587 MiB
// to 12,752 MiB. Reading a page that is already resident costs nothing.
//
// So the passes go cheap, then free-but-slow, then the one that makes no
// assumptions and is therefore the only one that can cost the player memory.
readonly record struct ScanPass(bool HeapSegmentsOnly, bool ResidentPagesOnly)
{
    // Heap segments, resident pages. ~0.4 GiB, ~100 ms, cannot inflate.
    public static readonly ScanPass Narrow = new(true, true);

    // Every region, resident pages. Costs time, still cannot inflate: this is
    // the answer to "the narrow pass guessed wrong about the allocator".
    public static readonly ScanPass Resident = new(false, true);

    // Every region, every page. Assumes nothing, finds anything, and is the
    // only pass that grows the game's resident set -- so it is rationed.
    public static readonly ScanPass Everything = new(false, false);

    public string Name =>
        HeapSegmentsOnly ? "narrow" : ResidentPagesOnly ? "resident" : "everything";
}

static class LockRecord
{
    // The window read from memory runs from the owner pointer to the kind.
    public const int Before = 0x08;
    public const int ReadSize = 0x40;
    private const ulong PageSize = 0x1000;


    // Offsets inside that window.
    private const int OwnerAt = 0x00;
    private const int UuidAt = 0x08;
    private const int Zero1At = 0x10;
    private const int Zero2At = 0x18;
    private const int PosAAt = 0x20;
    private const int PosBAt = 0x28;
    private const int Zero3At = 0x30;
    private const int KindAt = 0x38;

    public enum Kind
    {
        Auto = 0,
        Manual = 1,
    }

    public readonly record struct View(ulong Uuid, ulong Owner, Kind LockKind, bool Valid);

    // `window` must be ReadSize bytes read from (uuidAddress - Before).
    public static View Parse(ReadOnlySpan<byte> window)
    {
        if (window.Length < ReadSize) return default;

        ulong owner = BinaryPrimitives.ReadUInt64LittleEndian(window[OwnerAt..]);
        ulong uuid = BinaryPrimitives.ReadUInt64LittleEndian(window[UuidAt..]);
        ulong kind = BinaryPrimitives.ReadUInt64LittleEndian(window[KindAt..]);

        bool shape = Scanner.LooksLikeHeapPtr(owner)
            && Scanner.ZeroOrFlagPair(BinaryPrimitives.ReadUInt64LittleEndian(window[Zero1At..]))
            && Scanner.ZeroOrFlagPair(BinaryPrimitives.ReadUInt64LittleEndian(window[Zero2At..]))
            && Scanner.ZeroOrFlagPair(BinaryPrimitives.ReadUInt64LittleEndian(window[Zero3At..]))
            && Scanner.PositionField(BinaryPrimitives.ReadUInt64LittleEndian(window[PosAAt..]))
            && Scanner.PositionField(BinaryPrimitives.ReadUInt64LittleEndian(window[PosBAt..]))
            && kind <= 1;
        if (!shape) return default;

        return new View(uuid, owner, kind == 1 ? Kind.Manual : Kind.Auto, true);
    }

    // What the slot holds, without asking it to pass the discovery test.
    //
    // Parse is the signature the SCAN uses to pick one address out of
    // gigabytes, and it is deliberately strict. Using it to read an address
    // that is already known conflates two different questions, and the file
    // already says so about the uuid ("Parse cannot serve here: it is the
    // discovery test") -- but the poll went on calling it anyway and treating
    // a rejection as "nothing is locked".
    //
    // For an address whose owner pointer still matches, the fields are just
    // fields. This reads them.
    public readonly record struct Raw(ulong Owner, ulong Uuid, ulong KindWord);

    public static Raw ReadRaw(ReadOnlySpan<byte> window) => new(
        BinaryPrimitives.ReadUInt64LittleEndian(window[OwnerAt..]),
        BinaryPrimitives.ReadUInt64LittleEndian(window[UuidAt..]),
        BinaryPrimitives.ReadUInt64LittleEndian(window[KindAt..]));

    // Which parts of the signature a window fails, for the log. Named fields
    // rather than a hex dump: the question being asked is always "which test
    // rejected a record that plainly holds a lock", and a dump makes the
    // reader re-derive the answer.
    public static string Explain(ReadOnlySpan<byte> window)
    {
        if (window.Length < ReadSize) return "short";
        ulong owner = BinaryPrimitives.ReadUInt64LittleEndian(window[OwnerAt..]);
        ulong z1 = BinaryPrimitives.ReadUInt64LittleEndian(window[Zero1At..]);
        ulong z2 = BinaryPrimitives.ReadUInt64LittleEndian(window[Zero2At..]);
        ulong z3 = BinaryPrimitives.ReadUInt64LittleEndian(window[Zero3At..]);
        ulong pa = BinaryPrimitives.ReadUInt64LittleEndian(window[PosAAt..]);
        ulong pb = BinaryPrimitives.ReadUInt64LittleEndian(window[PosBAt..]);
        ulong kind = BinaryPrimitives.ReadUInt64LittleEndian(window[KindAt..]);
        var bad = new List<string>();
        if (!Scanner.LooksLikeHeapPtr(owner)) bad.Add("owner");
        if (!Scanner.ZeroOrFlagPair(z1)) bad.Add($"z1=0x{z1:x}");
        if (!Scanner.ZeroOrFlagPair(z2)) bad.Add($"z2=0x{z2:x}");
        if (!Scanner.ZeroOrFlagPair(z3)) bad.Add($"z3=0x{z3:x}");
        if (!Scanner.PositionField(pa)) bad.Add($"posA=0x{pa:x}");
        if (!Scanner.PositionField(pb)) bad.Add($"posB=0x{pb:x}");
        if (kind > 1) bad.Add($"kind=0x{kind:x}");
        return bad.Count == 0 ? "shape ok" : string.Join(" ", bad);
    }

    public static bool TryRead(IMemSource mem, ulong uuidAddress, byte[] scratch, out View view)
    {
        view = default;
        if (uuidAddress < Before) return false;
        if (!mem.Read(uuidAddress - Before, scratch, ReadSize, out int got) || got < ReadSize) return false;
        view = Parse(scratch);
        return view.Valid;
    }

    // Reads the slot without demanding that a lock be held.
    //
    // Measured 2026-09-19: the record sits at one address for the life of the
    // game process -- 0x230d47bb950 held every lock across a fifteen minute
    // session, through clears, target switches and a zone change. Only the
    // fields change. So once the address is known there is nothing to search
    // for, and the owner pointer is what says the slot is still ours.
    //
    // `Parse` cannot serve here: it is the discovery test, and it fails while
    // nothing is locked. Treating that as "the record is gone" is what made
    // the helper rescan all of memory every three seconds.
    public static bool TryReadSlot(IMemSource mem, ulong uuidAddress, byte[] scratch,
        out ulong owner, out View view) =>
        TryReadSlot(mem, uuidAddress, scratch, out owner, out view, out _);

    public static bool TryReadSlot(IMemSource mem, ulong uuidAddress, byte[] scratch,
        out ulong owner, out View view, out Raw raw)
    {
        owner = 0;
        view = default;
        raw = default;
        if (uuidAddress < Before) return false;
        if (!mem.Read(uuidAddress - Before, scratch, ReadSize, out int got) || got < ReadSize) return false;
        raw = ReadRaw(scratch);
        owner = raw.Owner;
        view = Parse(scratch);
        return true;
    }

    // One per worker thread, not one per region: a game process has thousands
    // of private regions and allocating these for each of them churns
    // gigabytes of large-object heap per scan.
    private sealed class Worker
    {
        public readonly byte[] Buf = new byte[Scanner.ChunkSize];
        // One extra: a chunk that starts part way into a page spans one more
        // page than its length divided by the page size.
        public readonly bool[] Resident = new bool[Scanner.ChunkSize / (int)PageSize + 1];
    }

    // Scans for the record. Candidates are addresses holding a uuid the packet
    // stream names: the structural test alone is strong, but the entity list
    // makes it decisive and keeps the candidate set to a few hundred.
    public static List<ulong> Find(IMemSource mem, KnownEntities known, out string diag,
        bool requireKnown = true, IDictionary<ulong, ulong>? owners = null,
        // `default` is (false, false) == Everything, which is what every
        // caller that does not care about cost wants: the analysis modes and
        // the tests read the whole snapshot. Verified in SelfTestHold.
        ScanPass pass = default)
    {
        var found = new List<(ulong Addr, ulong Owner)>();
        var gate = new object();
        int candidates = 0;
        int unreadable = 0;
        ulong skippedBytes = 0;

        var regions = mem.Regions(privateOnly: true);
        if (pass.HeapSegmentsOnly)
            regions = regions.FindAll(r => Scanner.HeapSegment(r.Size));
        ulong regionBytes = 0;
        foreach (var r in regions) regionBytes += r.Size;
        // Which region sizes actually held a record. The 16 MiB rule came
        // from eight snapshots and a live session then broke it, so the sizes
        // that work are worth measuring every time rather than assuming.
        var hitSizes = new SortedSet<ulong>();
        ulong readBytes = 0;
        ulong coldSkipped = 0;
        // Leave the game half the machine. Reading gigabytes through
        // ReadProcessMemory on every core starves it and shows up as stutter.
        int workers = Math.Max(2, Environment.ProcessorCount / 2);
        var opts = new ParallelOptions { MaxDegreeOfParallelism = workers };
        Parallel.ForEach(regions, opts,
            // One 4 MiB buffer per worker, not per region. A game process has
            // thousands of private regions, and allocating a large-object-heap
            // array for each of them churns gigabytes per scan.
            () => new Worker(),
            (region, _, w) =>
        {
            var buf = w.Buf;
            var (b, s) = region;
            var local = new List<(ulong, ulong)>();
            int localCand = 0;
            int localBad = 0;
            ulong localSkipped = 0;
            ulong localRead = 0;
            ulong localCold = 0;
            ulong pos = 0;
            while (pos < s)
            {
                int want = (int)Math.Min((ulong)Scanner.ChunkSize, s - pos);
                // Skip pages the target does not have resident. Reading one
                // would fault it in and leave it there, which is the whole
                // cost this pass exists to avoid; a source that cannot answer
                // (a snapshot, the test double) reads everything as before.
                if (pass.ResidentPagesOnly)
                {
                    // `pos` is not page-aligned after the overlap step, so ask
                    // about the page that contains it. Getting this wrong is
                    // silent: the query fails, the pass reads everything, and
                    // it looks like residency filtering that does nothing.
                    ulong here = b + pos;
                    ulong pageBase = here & ~(PageSize - 1);
                    int lead = (int)(here - pageBase);
                    int pages = (lead + want + (int)PageSize - 1) / (int)PageSize;
                    if (mem.TryResidency(pageBase, pages, w.Resident))
                    {
                        int first = 0;
                        while (first < pages && !w.Resident[first]) first++;
                        if (first >= pages)
                        {
                            // Nothing here is resident: skip the whole chunk.
                            localCold += (ulong)want;
                            pos += (ulong)want;
                            continue;
                        }
                        if (first > 0)
                        {
                            ulong to = pageBase + (ulong)first * PageSize;
                            localCold += to - here;
                            pos += to - here;
                            continue;
                        }
                        int run = 0;
                        while (run < pages && w.Resident[run]) run++;
                        want = Math.Min(want, run * (int)PageSize - lead);
                    }
                }
                if (!mem.Read(b + pos, buf, want, out int read) || read <= 0)
                {
                    // Nothing at all could be read here, so the page at `pos`
                    // itself is gone -- a region enumerated at the start of
                    // the scan can be freed before the scan reaches it. How
                    // far the damage runs is not knowable from the failure,
                    // and both ways of guessing are bad: one page per failed
                    // syscall walked a freed 400 MiB region in 15.2 seconds
                    // (measured 2026-09-19, skipped=98416), while jumping in
                    // wider strides skips live memory that may hold the
                    // record. So ask instead -- the live source answers from
                    // VirtualQueryEx in one call, and nothing readable is
                    // ever stepped over.
                    localBad++;
                    if (mem.TryNextReadable(b + pos, out ulong nextAddr) && nextAddr > b + pos)
                    {
                        ulong np = nextAddr - b;
                        localSkipped += np - pos;
                        pos = np;
                    }
                    else
                    {
                        // A source that cannot answer (a snapshot) steps.
                        localSkipped += PageSize;
                        pos += PageSize;
                    }
                    continue;
                }
                for (int off = Before; off + (ReadSize - Before) <= read; off += 8)
                {
                    ulong v = Scanner.U64(buf, off);
                    if (!UuidShape.IsLockable(v)) continue;
                    if (requireKnown && !known.Has(v)) continue;
                    localCand++;
                    var view = Parse(buf.AsSpan(off - Before, ReadSize));
                    if (view.Valid) local.Add((b + pos + (ulong)off, view.Owner));
                }
                localRead += (ulong)read;
                if ((ulong)read >= s - pos) break;
                // Overlap so a record straddling a chunk boundary is still
                // seen, and stay 8-aligned: a short read would otherwise shift
                // every later chunk and hide an aligned record behind it.
                ulong step = (ulong)Math.Max(8, read - ReadSize) & ~7UL;
                pos += Math.Max(8, step);
            }
            lock (gate)
            {
                found.AddRange(local);
                candidates += localCand;
                unreadable += localBad;
                skippedBytes += localSkipped;
                readBytes += localRead;
                coldSkipped += localCold;
                if (local.Count > 0) hitSizes.Add(s);
            }
            return w;
        },
            _ => { });

        // Regions complete in whatever order the workers finish them, and the
        // caller picks the first manual lock it sees. Sorting makes which one
        // that is the same from one scan to the next.
        found.Sort((x, y) => x.Addr.CompareTo(y.Addr));
        if (owners != null)
        {
            foreach (var (a, o) in found) owners[a] = o;
        }

        // The read volume is a cost the player pays in resident memory, not
        // just a number of milliseconds, so it is in every line: how much was
        // offered (reg), how much was actually read, and how much was left
        // cold. `in=` names the region sizes that held a record, which is the
        // only way to find out when the heap-segment guess stops being true.
        diag = $"{pass.Name} cand={candidates} rec={found.Count}"
             + $" reg={regions.Count}/{regionBytes >> 20}MiB read={readBytes >> 20}MiB"
             + (coldSkipped >= (1 << 20) ? $" cold={coldSkipped >> 20}MiB" : "")
             + (hitSizes.Count > 0 ? $" in={string.Join("/", hitSizes.Select(x => (x >> 20) + "MiB"))}" : "")
             + (unreadable > 0 ? $" gaps={unreadable} skipped={skippedBytes / PageSize}pg" : "");
        return found.Select(x => x.Addr).ToList();
    }
}
