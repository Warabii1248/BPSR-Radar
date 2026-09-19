using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

// Ground-truth memory sweeps, taken when the player marks a lock change.
//
// The first recording used whole-memory snapshots, which cost ~1 GB and 3.5
// seconds each: fine for discovering the structure, far too heavy to repeat
// once per lock. Finding the field that holds the lock target does not need
// the memory, only the addresses that currently hold an entity uuid -- a few
// hundred KB. That makes a sweep cheap enough to take on every mark, which is
// what turns "some address changed" into "this address changed exactly when
// the player locked something".
//
// Two kinds of hit are recorded:
//   V <addr> <uuid>   the address holds the uuid outright
//   P <addr> <uuid>   the address holds a pointer to the registry node of
//                     that uuid (a target stored by reference, not by value)
//
// File: mark-<seq>-<kind>-<ts>.tsv.gz next to the recording.
static class MarkScan
{
    // Bytes captured either side of a confirmed hit, to catch the flag that
    // tells a manual lock from the automatic provisional one.
    public const int WindowBefore = 0x40;
    public const int WindowAfter = 0x40;
    public const int WindowSize = WindowBefore + WindowAfter;
    private const int MaxWindows = 4000;

    public sealed class Mark
    {
        // The writer emits lower-case names. Naming them explicitly rather
        // than relying on a matching policy: the first run of this silently
        // deserialised every mark with n=0, so none ever looked new and not a
        // single sweep ran.
        [System.Text.Json.Serialization.JsonPropertyName("ts")]
        public long Ts { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("kind")]
        public string Kind { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("n")]
        public int N { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("target")]
        public long Target { get; set; }
    }

    // Reads marks.jsonl and returns any entries newer than `afterSeq`.
    public static List<Mark> ReadNew(string path, int afterSeq)
    {
        var list = new List<Mark>();
        try
        {
            if (!File.Exists(path)) return list;
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length == 0) continue;
                try
                {
                    var m = JsonSerializer.Deserialize<Mark>(line);
                    if (m != null && m.N > afterSeq && m.Kind.Length > 0) list.Add(m);
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    // Sweeps private memory for values that are plausible entity uuids, and
    // for pointers into the registry nodes those uuids live in.
    public static string? Sweep(IMemSource mem, KnownEntities known, string dir, Mark mark,
        Action<string>? log = null)
    {
        var sw = Stopwatch.StartNew();
        if (known.Count == 0)
        {
            log?.Invoke("mark-sweep warning: packet-side entity list is empty; " +
                        "monsters are still recorded by shape, everything else is not");
        }
        var regions = mem.Regions(privateOnly: true);

        // Pass 1: every address whose qword reads as a live entity uuid.
        var values = new List<(ulong Addr, ulong Uuid, bool Known)>();
        // Records are the registry nodes: +0x10 holds the uuid. Remember
        // their addresses so pass 2 can resolve a pointer back to an entity.
        var nodeOfAddr = new Dictionary<ulong, ulong>();
        var gate = new object();

        Parallel.ForEach(regions, region =>
        {
            var (b, s) = region;
            var buf = new byte[Scanner.ChunkSize];
            var localVals = new List<(ulong, ulong, bool)>();
            var localNodes = new List<(ulong, ulong)>();
            ulong pos = 0;
            while (pos < s)
            {
                int want = (int)Math.Min((ulong)Scanner.ChunkSize, s - pos);
                if (!mem.Read(b + pos, buf, want, out int read) || read <= 0)
                {
                    pos += (ulong)want;
                    continue;
                }
                for (int off = 0; off + 8 <= read; off += 8)
                {
                    ulong v = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(off, 8));
                    // Monsters are kept on shape alone. The entity list comes
                    // from the packet side and can be empty right after start
                    // or if capture is down, and a sweep that silently records
                    // nothing is worse than one that records a little extra.
                    bool confirmed = known.Has(v);
                    if (!confirmed && !UuidShape.IsMonster(v)) continue;
                    ulong addr = b + pos + (ulong)off;
                    // Shape alone lets bit patterns through: one sweep held
                    // 0x4000000040 seventy thousand times. The flag lets the
                    // differential insist on values the packet stream names.
                    localVals.Add((addr, v, confirmed));
                    // A registry node carries the uuid at +0x10 with a zero at
                    // +0x08 and +0x18; remember the node base for pass 2.
                    if (off >= 0x10 && off + 0x20 <= read
                        && BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(off - 0x08, 8)) == 0
                        && BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(off + 0x08, 8)) == 0)
                    {
                        localNodes.Add((addr - 0x10, v));
                    }
                }
                pos += (ulong)read;
            }
            if (localVals.Count > 0 || localNodes.Count > 0)
            {
                lock (gate)
                {
                    values.AddRange(localVals);
                    foreach (var (a, u) in localNodes) nodeOfAddr[a] = u;
                }
            }
        });

        // Pass 2: addresses holding a pointer to one of those nodes.
        var pointers = new List<(ulong Addr, ulong Uuid)>();
        if (nodeOfAddr.Count > 0)
        {
            Parallel.ForEach(regions, region =>
            {
                var (b, s) = region;
                var buf = new byte[Scanner.ChunkSize];
                var local = new List<(ulong, ulong)>();
                ulong pos = 0;
                while (pos < s)
                {
                    int want = (int)Math.Min((ulong)Scanner.ChunkSize, s - pos);
                    if (!mem.Read(b + pos, buf, want, out int read) || read <= 0)
                    {
                        pos += (ulong)want;
                        continue;
                    }
                    for (int off = 0; off + 8 <= read; off += 8)
                    {
                        ulong v = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(off, 8));
                        if (v != 0 && nodeOfAddr.TryGetValue(v, out ulong u))
                        {
                            local.Add((b + pos + (ulong)off, u));
                        }
                    }
                    pos += (ulong)read;
                }
                if (local.Count > 0) lock (gate) pointers.AddRange(local);
            });
        }

        try
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"mark-{mark.N:D3}-{mark.Kind}-{mark.Ts}.tsv.gz");
            using (var fs = File.Create(path))
            using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
            using (var w = new StreamWriter(gz))
            {
                w.WriteLine($"# mark n={mark.N} kind={mark.Kind} ts={mark.Ts} helperTarget=0x{mark.Target:x}");
                w.WriteLine($"# entities={known.Count} self=0x{known.SelfUuid:x} nodes={nodeOfAddr.Count}");
                foreach (var (a, u, k) in values) w.WriteLine($"V\t{a:x}\t{u:x}\t{(k ? 1 : 0)}");
                foreach (var (a, u) in pointers) w.WriteLine($"P\t{a:x}\t{u:x}\t1");

                // There are two lock states sharing one target slot: the
                // automatic provisional lock the game takes on whatever is in
                // range, and the manual lock the player sets deliberately.
                // Only the manual one is worth displaying, so the uuid alone
                // is not enough -- something nearby must say which it is.
                // Record a window around every confirmed hit so that flag can
                // be found by comparing marks of different kinds.
                var window = new byte[WindowSize];
                int windows = 0;
                foreach (var (a, _, k) in values)
                {
                    if (!k || windows >= MaxWindows) continue;
                    if (a < WindowBefore) continue;
                    if (!mem.Read(a - WindowBefore, window, WindowSize, out int got) || got < WindowSize) continue;
                    w.WriteLine($"W\t{a:x}\t{Convert.ToHexString(window)}");
                    windows++;
                }
            }
            sw.Stop();
            log?.Invoke($"mark-sweep n={mark.N} {mark.Kind} vals={values.Count}(" +
                        $"{values.Count(x => x.Known)} confirmed) ptrs={pointers.Count} " +
                        $"nodes={nodeOfAddr.Count} ms={sw.ElapsedMilliseconds} -> {Path.GetFileName(path)}");
            return path;
        }
        catch (Exception ex)
        {
            log?.Invoke($"mark-sweep failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
