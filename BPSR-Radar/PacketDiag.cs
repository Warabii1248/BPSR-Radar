using BpsrRadar.Capture;
using RadarProto;
using System.Buffers.Binary;
using System.IO;

namespace BpsrRadar;

// Packet-side half of the offline harness.
//
// Which packet (if any) announces a lock-on is still unknown: registering a
// handler for the one method that looked right produced zero events, which
// proves nothing about the other few hundred. Rather than guess again, this
// records every packet together with any value inside it that has the shape
// of an entity uuid. Correlating that against the helper's timestamped uuid
// transitions (lock-events.log) ranks every service/method by how often it
// carries the uuid the player just locked, so the answer falls out of the
// data instead of another hand-picked hypothesis.
//
// Modes are independent:
//   DumpPath set   -> one line per packet, for offline correlation.
//   WatchUuid()    -> live ring-buffer dump around a helper uuid transition.
internal static class PacketDiag
{
    private const int MaxEntries = 4000;
    private const double RingSeconds = 5;
    private const double WatchSeconds = 2.5;
    private const int MaxWatchLines = 300;

    private static readonly object gate = new();
    private static readonly Queue<Entry> ring = new();
    private static long watchUuid;
    private static DateTime watchUntilUtc = DateTime.MinValue;
    private static int watchLines;
    private static long observed;

    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "BPSR-Radar", "lock-pkt-diag.log");

    // Set from the command line (--dump-packets <path>) to record every packet.
    public static string? DumpPath;

    // The ring buffer and the watch windows it dumps around a lock change are
    // development instrumentation: a few hours of play wrote 3.9 MB across
    // 54,470 lines, and nothing ever rotates or deletes it. A player did not
    // ask for a record of their packet traffic on disk, so this is off unless
    // a harness switch turns it on.
    public static bool DiagLog;
    private static StreamWriter? dump;

    private struct Entry
    {
        public DateTime T;
        public ObservedPacketFlow Flow;
        public ulong Svc;
        public uint Method;
        public byte[] Payload;
    }

    public static void WatchUuid(long uuid)
    {
        lock (gate)
        {
            watchUuid = uuid;
            // The window is compared against wall-clock time, so anchor it on
            // the same clock the packets are stamped with rather than mixing
            // UtcNow with a capture timestamp that may be local.
            watchUntilUtc = DateTime.UtcNow.AddSeconds(WatchSeconds);
            watchLines = 0;
            Append($"--- watch uuid=0x{uuid:x} observed={observed} ---");
            foreach (var e in ring)
            {
                Emit(e, uuid);
            }
        }
    }

    public static void Observe(ObservedPacketFlow flow, ulong serviceId, uint methodId,
        ReadOnlySpan<byte> payload, DateTime packetTimeUtc)
    {
        Interlocked.Increment(ref observed);

        if (DumpPath != null)
        {
            WriteDumpLine(flow, serviceId, methodId, payload, packetTimeUtc);
        }

        var e = new Entry
        {
            T = packetTimeUtc,
            Flow = flow,
            Svc = serviceId,
            Method = methodId,
            Payload = payload.ToArray(),
        };
        lock (gate)
        {
            ring.Enqueue(e);
            while (ring.Count > MaxEntries ||
                   (ring.Count > 1 && (packetTimeUtc - ring.Peek().T).TotalSeconds > RingSeconds))
            {
                ring.Dequeue();
            }

            if (watchUuid != 0 && DateTime.UtcNow <= watchUntilUtc)
            {
                Emit(e, watchUuid);
            }
        }
    }

    // How many packets the observer has seen at all. An empty correlation log
    // means "no packet carried the uuid"; a zero here means the observer never
    // ran, which is a different problem entirely.
    public static long ObservedCount => Interlocked.Read(ref observed);

    private static void WriteDumpLine(ObservedPacketFlow flow, ulong serviceId, uint methodId,
        ReadOnlySpan<byte> payload, DateTime packetTimeUtc)
    {
        var uuids = ExtractUuids(payload);
        lock (gate)
        {
            try
            {
                if (dump == null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(DumpPath!)!);
                    dump = new StreamWriter(DumpPath!, append: false) { AutoFlush = false };
                    dump.WriteLine("# ts_utc_ms\tflow\tsvc\tmethod\tlen\tuuids");
                }
                dump.Write(new DateTimeOffset(packetTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds());
                dump.Write('\t');
                dump.Write(flow);
                dump.Write("\t0x");
                dump.Write(serviceId.ToString("x"));
                dump.Write("\t0x");
                dump.Write(methodId.ToString("x"));
                dump.Write('\t');
                dump.Write(payload.Length);
                dump.Write('\t');
                if (uuids.Count > 0)
                {
                    for (int i = 0; i < uuids.Count; i++)
                    {
                        if (i > 0) dump.Write(',');
                        dump.Write("0x");
                        dump.Write(uuids[i].ToString("x"));
                    }
                }
                dump.Write('\n');
                // Flush periodically so a crash or a forced quit still leaves
                // a usable correlation log behind.
                if ((observed & 0x3FF) == 0) dump.Flush();
            }
            catch
            {
            }
        }
    }

    public static void Flush()
    {
        lock (gate)
        {
            try { dump?.Flush(); } catch { }
        }
    }

    public static void Close()
    {
        lock (gate)
        {
            try { dump?.Flush(); dump?.Dispose(); } catch { }
            dump = null;
        }
    }

    // Entity uuid shape, mirrored from RadarTracker.EntityIdToUuid:
    //   uuid = entityId << 16 | entityType << 6, bit 15 set for summons.
    // The upper bound keeps ordinary large numbers (timestamps, ids) out.
    private static bool IsEntityUuid(ulong v)
    {
        if ((v & 0x3F) != 0) return false;
        if (v < 0x10000 || v >= (1UL << 40)) return false;
        int t = (int)((v >> 6) & 0x1FF);
        return t > 0 && t < (int)EEntityType.EntCount;
    }

    // Collects every varint and fixed64 in the message (recursing into nested
    // messages) that could be an entity uuid. Protobuf is not byte aligned, so
    // decoding the wire format finds values a raw memory scan would miss.
    private static List<ulong> ExtractUuids(ReadOnlySpan<byte> data)
    {
        var found = new List<ulong>();
        Walk(data, found, 0);
        if (found.Count > 1)
        {
            found = found.Distinct().ToList();
        }
        return found;
    }

    private static void Walk(ReadOnlySpan<byte> data, List<ulong> found, int depth)
    {
        if (depth > 3 || found.Count > 32) return;
        int offset = 0;
        while (offset < data.Length)
        {
            if (!TryReadVarint(data, ref offset, out ulong tag) || tag == 0) return;
            int wire = (int)(tag & 7);
            if (wire == 0)
            {
                if (!TryReadVarint(data, ref offset, out ulong v)) return;
                if (IsEntityUuid(v)) found.Add(v);
            }
            else if (wire == 1)
            {
                if (offset + 8 > data.Length) return;
                ulong v = BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
                if (IsEntityUuid(v)) found.Add(v);
                offset += 8;
            }
            else if (wire == 2)
            {
                if (!TryReadVarint(data, ref offset, out ulong len) || len > (ulong)(data.Length - offset)) return;
                Walk(data.Slice(offset, (int)len), found, depth + 1);
                offset += (int)len;
            }
            else if (wire == 5)
            {
                if (offset + 4 > data.Length) return;
                offset += 4;
            }
            else
            {
                return;
            }
        }
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int offset, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (offset < data.Length && shift < 70)
        {
            byte b = data[offset++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }

    private static void Emit(in Entry e, long uuid)
    {
        if (watchLines >= MaxWatchLines)
        {
            return;
        }

        var le8 = BitConverter.GetBytes(uuid);
        var le4 = BitConverter.GetBytes((uint)(uuid & 0xFFFFFFFF));
        bool has8 = e.Payload.AsSpan().IndexOf(le8) >= 0;
        bool has4 = !has8 && e.Payload.AsSpan().IndexOf(le4) >= 0;
        bool hasField = ExtractUuids(e.Payload).Contains(unchecked((ulong)uuid));
        string hex = "";
        if (has8 || has4 || hasField)
        {
            hex = " hex=" + Convert.ToHexString(e.Payload[..Math.Min(64, e.Payload.Length)]);
        }

        watchLines++;
        Append($"{e.T:HH:mm:ss.fff} {e.Flow} svc=0x{e.Svc:x} m=0x{e.Method:x} len={e.Payload.Length}" +
               (hasField ? " FIELD" : "") + (has8 ? " U8" : has4 ? " U4" : "") + hex);
    }

    public static void Note(string line)
    {
        lock (gate)
        {
            Append($"{DateTime.Now:HH:mm:ss.fff} {line}");
        }
    }

    private static void Append(string line)
    {
        if (!DiagLog) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, line + "\n");
        }
        catch { }
    }
}
