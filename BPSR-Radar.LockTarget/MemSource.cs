using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

// Memory access abstraction for the lock-target scanner.
//
// The scanner used to call ReadProcessMemory directly, which meant every
// experiment needed the game running, a manual dungeon run and a pair of eyes
// on the overlay. Routing it through IMemSource lets the exact same scanning
// code run against a captured snapshot file instead, so an algorithm change
// can be verified offline, repeatedly, with a negative control (run the old
// build against the same snapshot and watch it fail).
//
// Snapshot file format ("BPSRMEM1"), little endian throughout:
//   magic      8 bytes  "BPSRMEM1"
//   version    u32      1
//   flags      u32      1 = blocks are Deflate compressed
//   regions    u32      region table entry count
//   blocks     u32      block table entry count
//   capturedAt i64      unix ms, UTC
//   rawBytes   u64      total uncompressed payload bytes
//   region table: regions x (u64 base, u64 size)
//   block table:  blocks  x (u64 addr, u32 rawLen, u32 compLen, u64 fileOffset)
//   payload blobs at their fileOffset
//
// Only MEM_PRIVATE committed readable regions are captured: game heap objects
// always live there, and skipping mapped asset regions keeps a snapshot to a
// few hundred MB instead of several GB. A snapshot therefore cannot reproduce
// the scanner's occasional all-region-types pass.
interface IMemSource
{
    // privateOnly is honoured by the live source; a snapshot always returns
    // the regions it captured (MEM_PRIVATE only, see above).
    List<(ulong Base, ulong Size)> Regions(bool privateOnly);

    bool Read(ulong addr, byte[] buffer, int length, out int read);

    // Where the next readable byte at or after `addr` is. False when the
    // source cannot answer, and the caller steps a page instead.
    //
    // A failed read says nothing about how far the damage extends, and both
    // ways of guessing are bad. Stepping one page per failed syscall walked a
    // freed 400 MiB region in 15.2 seconds of a scan that normally takes one
    // (measured 2026-09-19, skipped=98416). Striding past it in wider jumps
    // is fast but skips live memory -- up to 60 KiB of it -- which may be
    // exactly where the record sits. The live source does not have to guess:
    // VirtualQueryEx answers in one call.
    bool TryNextReadable(ulong addr, out ulong next);

    // Which of the `pages` pages starting at `addr` the target already has in
    // its working set. False when the source cannot tell, and the caller
    // reads everything -- the old behaviour.
    //
    // This exists because reading is not free for the process being read.
    // ReadProcessMemory faults each page it touches into the TARGET's working
    // set and it does not leave on its own: measured 2026-09-19, a full scan
    // took the game from 5,587 MiB resident to 12,752 MiB in one pass, which
    // is what a player sees in Task Manager. Pages that are already resident
    // cost nothing to read, so a pass restricted to them cannot inflate
    // anything, whatever it reads.
    bool TryResidency(ulong addr, int pages, bool[] resident) => false;

    string Describe { get; }
}

sealed class LiveMem : IMemSource
{
    private readonly IntPtr handle;

    public LiveMem(IntPtr handle) => this.handle = handle;

    public IntPtr Handle => handle;

    public string Describe => $"live handle=0x{handle:x}";

    public List<(ulong Base, ulong Size)> Regions(bool privateOnly)
    {
        var regions = new List<(ulong, ulong)>();
        ulong addr = 0;
        while (addr < 0x7FFFFFFFFFFF)
        {
            nint r = Native.VirtualQueryEx(handle, (IntPtr)addr, out var mbi,
                (nuint)Marshal.SizeOf<Native.MEMORY_BASIC_INFORMATION64>());
            if (r == 0 || mbi.RegionSize == 0) break;
            if (IsReadable(mbi, privateOnly)) regions.Add((mbi.BaseAddress, mbi.RegionSize));
            addr = mbi.BaseAddress + mbi.RegionSize;
        }
        return regions;
    }

    public static bool IsReadable(in Native.MEMORY_BASIC_INFORMATION64 mbi, bool privateOnly) =>
        mbi.State == Native.MEM_COMMIT
        && (mbi.Protect & (Native.PAGE_GUARD | Native.PAGE_NOACCESS)) == 0
        && (mbi.Protect & (Native.PAGE_READWRITE | Native.PAGE_READONLY | Native.PAGE_WRITECOPY |
                           Native.PAGE_EXECUTE_READ | Native.PAGE_EXECUTE_READWRITE | Native.PAGE_EXECUTE_WRITECOPY)) != 0
        && (!privateOnly || mbi.Type == Native.MEM_PRIVATE);

    public bool Read(ulong addr, byte[] buffer, int length, out int read)
    {
        // A partial copy is a success for our purposes. ReadProcessMemory
        // returns FALSE with ERROR_PARTIAL_COPY when the range runs into
        // unreadable memory part way, but it still fills in everything up to
        // that point -- and the old `ok && n > 0` threw all of it away. With
        // a 4 MiB request, one bad page near the end discarded almost four
        // megabytes of perfectly readable memory, every chunk, every scan.
        // The caller already works off `read` rather than the length it
        // asked for.
        Native.ReadProcessMemory(handle, (IntPtr)addr, buffer, length, out nint n);
        read = (int)n;
        return n > 0;
    }

    // PSAPI_WORKING_SET_EX_INFORMATION is { PVOID VirtualAddress;
    // ULONG_PTR VirtualAttributes; } -- 16 bytes on x64, and bit 0 of the
    // attributes is Valid, meaning the page is in the target's working set.
    // One call answers for as many pages as the buffer holds.
    public bool TryResidency(ulong addr, int pages, bool[] resident)
    {
        if (pages <= 0 || pages > resident.Length) return false;
        int bytes = pages * 16;
        IntPtr info = Marshal.AllocHGlobal(bytes);
        try
        {
            for (int i = 0; i < pages; i++)
                Marshal.WriteInt64(info, i * 16, unchecked((long)(addr + (ulong)i * 0x1000)));
            if (!Native.QueryWorkingSetEx(handle, info, bytes)) return false;
            for (int i = 0; i < pages; i++)
                resident[i] = (Marshal.ReadInt64(info, i * 16 + 8) & 1) != 0;
            return true;
        }
        catch { return false; }
        finally { Marshal.FreeHGlobal(info); }
    }

    public bool TryNextReadable(ulong addr, out ulong next)
    {
        next = 0;
        ulong probe = addr;
        // Bounded. A pathological address space must not turn one failed
        // read into an unbounded walk of the region list.
        for (int i = 0; i < 64; i++)
        {
            nint r = Native.VirtualQueryEx(handle, (IntPtr)probe, out var mbi,
                (nuint)Marshal.SizeOf<Native.MEMORY_BASIC_INFORMATION64>());
            if (r == 0 || mbi.RegionSize == 0) return false;
            // privateOnly matches how Find enumerated in the first place: if
            // the range stopped being private heap it is not ours to scan.
            if (IsReadable(mbi, privateOnly: true))
            {
                next = Math.Max(addr, mbi.BaseAddress);
                return true;
            }
            ulong after = mbi.BaseAddress + mbi.RegionSize;
            if (after <= probe) return false;
            probe = after;
        }
        return false;
    }
}

sealed class SnapshotMem : IMemSource, IDisposable
{
    // A snapshot has no live page tables to consult; the caller steps.
    public bool TryNextReadable(ulong addr, out ulong next) { next = 0; return false; }

    private readonly List<(ulong Base, ulong Size)> regions;
    private ulong[] blockAddrs = Array.Empty<ulong>();
    private byte[][] blockData = Array.Empty<byte[]>();

    public string Path { get; }
    public DateTime CapturedAtUtc { get; }
    public ulong RawBytes { get; }
    public string Describe => $"snapshot {System.IO.Path.GetFileName(Path)}";

    public SnapshotMem(string path)
    {
        Path = path;
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);

        var magic = br.ReadBytes(8);
        if (magic.Length != 8 || System.Text.Encoding.ASCII.GetString(magic) != MemSnapshot.Magic)
        {
            throw new InvalidDataException($"{path}: not a BPSRMEM1 snapshot");
        }
        uint version = br.ReadUInt32();
        if (version != MemSnapshot.Version)
        {
            throw new InvalidDataException($"{path}: unsupported snapshot version {version}");
        }
        uint flags = br.ReadUInt32();
        int regionCount = (int)br.ReadUInt32();
        int blockCount = (int)br.ReadUInt32();
        CapturedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(br.ReadInt64()).UtcDateTime;
        RawBytes = br.ReadUInt64();

        regions = new List<(ulong, ulong)>(regionCount);
        for (int i = 0; i < regionCount; i++)
        {
            ulong b = br.ReadUInt64();
            ulong s = br.ReadUInt64();
            regions.Add((b, s));
        }

        var addrs = new ulong[blockCount];
        var rawLens = new int[blockCount];
        var compLens = new int[blockCount];
        var offsets = new long[blockCount];
        for (int i = 0; i < blockCount; i++)
        {
            addrs[i] = br.ReadUInt64();
            rawLens[i] = (int)br.ReadUInt32();
            compLens[i] = (int)br.ReadUInt32();
            offsets[i] = (long)br.ReadUInt64();
        }

        var data = new byte[blockCount][];
        var compressed = (flags & MemSnapshot.FlagDeflate) != 0;
        for (int i = 0; i < blockCount; i++)
        {
            fs.Position = offsets[i];
            var blob = br.ReadBytes(compLens[i]);
            if (!compressed)
            {
                data[i] = blob;
                continue;
            }
            var raw = new byte[rawLens[i]];
            using var ms = new MemoryStream(blob, writable: false);
            using var ds = new DeflateStream(ms, CompressionMode.Decompress);
            int filled = 0;
            while (filled < raw.Length)
            {
                int n = ds.Read(raw, filled, raw.Length - filled);
                if (n <= 0) break;
                filled += n;
            }
            data[i] = raw;
        }

        // Sorted by address so Read can binary search and stitch across the
        // artificial 32 MB block boundaries inside a single region.
        var order = Enumerable.Range(0, blockCount).OrderBy(i => addrs[i]).ToArray();
        blockAddrs = order.Select(i => addrs[i]).ToArray();
        blockData = order.Select(i => data[i]).ToArray();
    }

    public List<(ulong Base, ulong Size)> Regions(bool privateOnly) => new(regions);

    public bool Read(ulong addr, byte[] buffer, int length, out int read)
    {
        read = 0;
        while (read < length)
        {
            ulong want = addr + (ulong)read;
            int bi = FindBlock(want);
            if (bi < 0) break;
            var blk = blockData[bi];
            ulong blkBase = blockAddrs[bi];
            int inner = (int)(want - blkBase);
            if (inner >= blk.Length) break;
            int take = Math.Min(length - read, blk.Length - inner);
            Buffer.BlockCopy(blk, inner, buffer, read, take);
            read += take;
        }
        return read > 0;
    }

    private int FindBlock(ulong addr)
    {
        int lo = 0, hi = blockAddrs.Length - 1, best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (blockAddrs[mid] <= addr) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (best < 0) return -1;
        return addr < blockAddrs[best] + (ulong)blockData[best].Length ? best : -1;
    }

    public void Dispose()
    {
        blockAddrs = Array.Empty<ulong>();
        blockData = Array.Empty<byte[]>();
        GC.Collect();
    }
}

static class MemSnapshot
{
    public const string Magic = "BPSRMEM1";
    public const uint Version = 1;
    public const uint FlagDeflate = 1;

    private const int BlockSize = 32 * 1024 * 1024;

    // Writes every MEM_PRIVATE committed readable region of the target to
    // `path`. Blocks are compressed in waves of ProcessorCount so peak memory
    // stays near one wave (~0.5 GB) instead of the whole dump.
    // Returns the number of bytes written, or -1 if the capture was refused.
    public static long Write(LiveMem mem, string path, Action<string>? log = null)
    {
        var regions = mem.Regions(privateOnly: true);
        var blocks = new List<(ulong Addr, int Len)>();
        ulong totalRaw = 0;
        foreach (var (b, s) in regions)
        {
            ulong pos = 0;
            while (pos < s)
            {
                int len = (int)Math.Min((ulong)BlockSize, s - pos);
                blocks.Add((b + pos, len));
                totalRaw += (ulong)len;
                pos += (ulong)len;
            }
        }

        // Measure free space before taking it: a refused capture that says so
        // is far better than one that fills the disk mid-dungeon.
        string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        try
        {
            var drive = new DriveInfo(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path))!);
            long needed = (long)(totalRaw / 2) + (512L << 20); // assume >=2x, plus margin
            if (drive.AvailableFreeSpace < needed)
            {
                log?.Invoke($"snapshot refused: need ~{needed >> 20} MiB, free {drive.AvailableFreeSpace >> 20} MiB");
                return -1;
            }
        }
        catch
        {
            // Unmeasurable free space is not proof that it fits, but refusing
            // here would lose the capture entirely; note it and continue.
            log?.Invoke("snapshot: free space unmeasurable, continuing");
        }

        var sw = Stopwatch.StartNew();
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var bw = new BinaryWriter(fs);

        bw.Write(System.Text.Encoding.ASCII.GetBytes(Magic));
        bw.Write(Version);
        bw.Write(FlagDeflate);
        bw.Write((uint)regions.Count);
        bw.Write((uint)blocks.Count);
        bw.Write(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        bw.Write(totalRaw);
        foreach (var (b, s) in regions) { bw.Write(b); bw.Write(s); }

        long tableOffset = fs.Position;
        // Reserve the block table; rewritten once the payload offsets are known.
        for (int i = 0; i < blocks.Count; i++)
        {
            bw.Write(0UL); bw.Write(0u); bw.Write(0u); bw.Write(0UL);
        }
        bw.Flush();

        var outAddr = new ulong[blocks.Count];
        var outRaw = new uint[blocks.Count];
        var outComp = new uint[blocks.Count];
        var outOff = new ulong[blocks.Count];

        int wave = Math.Max(1, Environment.ProcessorCount);
        var pending = new byte[wave][];
        var pendingRaw = new int[wave];

        for (int start = 0; start < blocks.Count; start += wave)
        {
            int count = Math.Min(wave, blocks.Count - start);
            Parallel.For(0, count, k =>
            {
                var (addr, len) = blocks[start + k];
                var raw = new byte[len];
                if (!mem.Read(addr, raw, len, out int got) || got <= 0)
                {
                    pending[k] = Array.Empty<byte>();
                    pendingRaw[k] = 0;
                    return;
                }
                using var ms = new MemoryStream(got / 2 + 1024);
                using (var ds = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                {
                    ds.Write(raw, 0, got);
                }
                pending[k] = ms.ToArray();
                pendingRaw[k] = got;
            });

            for (int k = 0; k < count; k++)
            {
                int idx = start + k;
                if (pending[k] == null || pendingRaw[k] == 0)
                {
                    outRaw[idx] = 0;
                    continue;
                }
                outAddr[idx] = blocks[idx].Addr;
                outRaw[idx] = (uint)pendingRaw[k];
                outComp[idx] = (uint)pending[k].Length;
                outOff[idx] = (ulong)fs.Position;
                fs.Write(pending[k], 0, pending[k].Length);
                pending[k] = null!;
            }
        }
        bw.Flush();

        // Drop unreadable blocks: a replay Read over that address must fail
        // the same way the live ReadProcessMemory did.
        int kept = 0;
        long end = fs.Position;
        fs.Position = tableOffset;
        for (int i = 0; i < blocks.Count; i++)
        {
            if (outRaw[i] == 0) continue;
            bw.Write(outAddr[i]); bw.Write(outRaw[i]); bw.Write(outComp[i]); bw.Write(outOff[i]);
            kept++;
        }
        bw.Flush();

        // Rewrite the real block count now that unreadable ones are gone.
        fs.Position = 8 + 4 + 4 + 4;
        bw.Write((uint)kept);
        bw.Flush();
        fs.Position = end;

        sw.Stop();
        log?.Invoke($"snapshot wrote {end >> 20} MiB raw={totalRaw >> 20} MiB blocks={kept}/{blocks.Count} ms={sw.ElapsedMilliseconds}");
        return end;
    }
}
