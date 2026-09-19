using System.Linq;
using Google.Protobuf.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Numerics;
using System.Text.Json;
using RadarProto;

namespace BpsrRadar;

[Flags]
internal enum EntitySource
{
    None = 0,
    Aoi = 1,
    PartySync = 2,
}

internal enum RadarEntityKind
{
    Self,
    Party,
    Player,
    Gimmick,
    Monster,
    Elite,
    Boss,
    UnknownMonster,
}

internal sealed record RadarEntitySnapshot(
    long Uuid,
    RadarEntityKind Kind,
    EntitySource Sources,
    Vector3 Position,
    float? HeadingRadians,
    string Name,
    long Hp,
    long MaxHp,
    bool HasHp,
    float DistanceFromSelf,
    float Opacity);

internal sealed record RadarSnapshot(
    uint SceneId,
    string SceneName,
    RadarEntitySnapshot? Self,
    RadarEntitySnapshot[] Party,
    RadarEntitySnapshot[] Players,
    RadarEntitySnapshot[] Enemies);

// Merged port of TacticalMapManager + the small part of EncounterManager's
// attribute accumulation that the map needs (name, type, hp, position).
internal static class RadarTracker
{
    private static readonly object Sync = new();
    private static readonly Dictionary<long, TrackedEntity> Entities = new();
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromMilliseconds(100);
    // The server announces an entity leaving AOI explicitly, so this is only
    // a safety net for an announcement that never arrives. It used to be 30
    // seconds, which quietly deleted anything that stopped moving: a monster
    // standing still sends no delta, so it vanished from the radar half a
    // minute after appearing. That is precisely the case the radar is for --
    // picking the sturdiest enemy out of a camp before pulling it, while
    // everything in the camp is still idle.
    private static readonly TimeSpan AoiExpiry = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinimumPartyExpiry = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FadeDelay = TimeSpan.FromSeconds(2);
    // Monsters legitimately go quiet while idle, so they keep full opacity
    // far longer before the display starts hinting the position is stale.
    private static readonly TimeSpan IdleFadeDelay = TimeSpan.FromSeconds(30);

    private static TimeSpan partyExpiry = MinimumPartyExpiry;
    private static long lastPartyBatchTimestamp;
    private static long lastSnapshotTimestamp;
    private static bool hasSnapshot;
    private static uint sceneId;
    private static string sceneName = "";
    private static RadarSnapshot cachedSnapshot = new(0, "", null, [], [], []);

    public static long PlayerUuid;
    public static long PlayerUid;

    // Persistent uuid->name cache (parity with upstream EntityCache): names
    // learned from packets are kept across sessions so entities already
    // present when capture starts can still be resolved.
    private static readonly ConcurrentDictionary<long, string> PersistentNames = new();
    private static readonly string NameCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BPSR-Radar", "namecache.json");
    private static int namesDirty;
    private static CancellationTokenSource? nameCacheCts;

    public static void LoadNameCache()
    {
        try
        {
            if (File.Exists(NameCachePath))
            {
                var dict = JsonSerializer.Deserialize<Dictionary<long, string>>(File.ReadAllText(NameCachePath));
                if (dict != null)
                {
                    foreach (var kv in dict) PersistentNames[kv.Key] = kv.Value;
                }
            }
        }
        catch { }

        nameCacheCts = new CancellationTokenSource();
        var ct = nameCacheCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(5000, ct); } catch (TaskCanceledException) { break; }
                FlushNameCache();
            }
        });
    }

    public static void SaveNameCache()
    {
        try { nameCacheCts?.Cancel(); } catch { }
        FlushNameCache();
    }

    private static void FlushNameCache()
    {
        if (Interlocked.Exchange(ref namesDirty, 0) == 0)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(NameCachePath)!);
            File.WriteAllText(NameCachePath, JsonSerializer.Serialize(PersistentNames));
        }
        catch { Interlocked.Exchange(ref namesDirty, 1); }
    }

    private static void RecordName(long uuid, string name)
    {
        if (uuid == 0 || string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        if (PersistentNames.TryGetValue(uuid, out var cur) && cur == name)
        {
            return;
        }
        PersistentNames[uuid] = name;
        Interlocked.Exchange(ref namesDirty, 1);
    }

    private static readonly object DirectionLock = new();
    private static float lastPositionDirection;
    private static long positionDirectionCount;

    public static long PositionDirectionCount => Interlocked.Read(ref positionDirectionCount);

    // When the scene last changed, in unix ms. Zero until the first one.
    public static long SceneChangedAtMs { get; private set; }

    // Published to the helper. It cannot see packets, so without this it has
    // no way to know the world was rebuilt under the record it is watching.
    public static uint CurrentSceneId { get { lock (Sync) { return sceneId; } } }

    public static void SetScene(uint newSceneId)
    {
        // A local, not a field: two threads calling SetScene would otherwise
        // lose one line and print the other twice. Only one packet thread
        // exists today, which is exactly the kind of assumption that stops
        // being true quietly.
        string? line = null;
        lock (Sync)
        {
            if (sceneId == newSceneId)
            {
                return;
            }

            uint sceneIdBefore = sceneId;
            sceneId = newSceneId;
            sceneName = GameDataTables.Scenes.TryGetValue((int)newSceneId, out var scene) ? scene.Name : "";
            lock (DirectionLock)
            {
                Interlocked.Exchange(ref positionDirectionCount, 0);
                lastPositionDirection = 0;
            }
            // Recorded before the wipe, because what it wipes is the
            // question. If an AOI batch for the new scene arrives before this
            // notification does -- and it can, SetScene is driven by a social
            // packet that merely carries SceneData, not by the scene load --
            // then everything it just delivered is discarded here, and a
            // static entity like a training dummy never re-announces itself.
            int cleared = Entities.Count;
            int clearedLockable = 0;
            foreach (var e in Entities.Values)
            {
                if (e.EntityType == EEntityType.EntMonster) clearedLockable++;
            }
            Entities.Clear();
            lastPartyBatchTimestamp = 0;
            partyExpiry = MinimumPartyExpiry;
            hasSnapshot = false;
            line = $"scene {sceneIdBefore}->{newSceneId} cleared={cleared} mon={clearedLockable}";
            // Read by EntityExport. Clearing the list here is what strands the
            // memory helper after a field transition: it cannot re-adopt the
            // record or scan for it until the exported list names a monster
            // again, so for the next few seconds that file is on the critical
            // path and is written more often.
            SceneChangedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        // Outside the lock: this one appends to a file, and Sync is taken by
        // the UI snapshot ten times a second.
        if (line != null) TrackerLog.Write(line);
    }

    public static void SetSelf(long uuid)
    {
        PlayerUuid = uuid;
        PlayerUid = UuidToEntityId(uuid);
    }

    public static void NoteDirection(float degrees)
    {
        lock (DirectionLock)
        {
            lastPositionDirection = degrees;
            Interlocked.Increment(ref positionDirectionCount);
        }
    }

    public static void ProcessAttrs(long uuid, RepeatedField<Attr> attrs)
    {
        if (uuid == 0)
        {
            return;
        }

        var entity = GetOrCreate(uuid);
        lock (Sync)
        {
            foreach (var attr in attrs)
            {
                if (attr.Id == 0 || attr.RawData == null)
                {
                    continue;
                }

                var reader = new Google.Protobuf.CodedInputStream(attr.RawData.ToByteArray());
                bool isNoValue = attr.RawData.Length == 0;
                switch ((EAttrType)attr.Id)
                {
                    case EAttrType.AttrName:
                        entity.Name = isNoValue ? "" : reader.ReadString().TrimEnd();
                        RecordName(uuid, entity.Name);
                        break;
                    case EAttrType.AttrId:
                        entity.TypeUid = isNoValue ? 0 : reader.ReadInt32();
                        ApplyTableLookup(entity);
                        break;
                    case EAttrType.AttrHp:
                        entity.Hp = isNoValue ? 0L : reader.ReadInt64();
                        entity.HasCurrentHp = true;
                        entity.HasHp = true;
                        break;
                    case EAttrType.AttrMaxHp:
                        entity.MaxHp = isNoValue ? 0L : reader.ReadInt64();
                        entity.HasHp = true;
                        break;
                    case EAttrType.AttrPos:
                        var pos = isNoValue ? new Vec3() : Vec3.Parser.ParseFrom(reader);
                        entity.AoiPosition = new Vector3(pos.X, pos.Y, pos.Z);
                        entity.HasAoiPosition = true;
                        break;
                }
            }
        }
    }

    public static void SetEntityName(long uuid, string name)
    {
        if (uuid == 0 || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var entity = GetOrCreate(uuid);
        lock (Sync)
        {
            entity.Name = name;
        }
        RecordName(uuid, name);
    }

    public static void SetEntityType(long uuid, EEntityType entityType)
    {
        var entity = GetOrCreate(uuid);
        lock (Sync)
        {
            entity.EntityType = entityType;
            ApplyTableLookup(entity);
        }
    }

    private static void ApplyTableLookup(TrackedEntity entity)
    {
        if (entity.TypeUid == 0)
        {
            return;
        }

        if (entity.EntityType == EEntityType.EntMonster &&
            GameDataTables.Monsters.TryGetValue(entity.TypeUid, out var monster))
        {
            entity.Name = monster.Name;
            entity.MonsterClass = monster.IsGimmick ? RadarEntityKind.Gimmick : monster.MonsterType switch
            {
                1 => RadarEntityKind.Elite,
                2 => RadarEntityKind.Boss,
                _ => RadarEntityKind.Monster,
            };
        }
        else if (entity.EntityType == EEntityType.EntDummy &&
                 GameDataTables.Dummys.TryGetValue(entity.TypeUid, out var dummy))
        {
            entity.Name = dummy.Name;
        }
        RecordName(entity.Uuid, entity.Name);
    }

    public static void ObserveAoi(long uuid)
    {
        if (uuid == 0)
        {
            return;
        }

        lock (Sync)
        {
            if (!Entities.TryGetValue(uuid, out var tracked))
            {
                return;
            }

            tracked.Sources |= EntitySource.Aoi;
            tracked.IsSummon = IsSummonByUuid(uuid);
            tracked.AoiSceneId = sceneId;
            tracked.LastAoiTimestamp = TimeProvider.System.GetTimestamp();
        }
    }

    public static void RemoveAoi(long uuid)
    {
        lock (Sync)
        {
            if (!Entities.TryGetValue(uuid, out var tracked))
            {
                return;
            }

            tracked.Sources &= ~EntitySource.Aoi;
            RemoveIfUnobserved(uuid, tracked);
        }
    }

    public static void BeginPartySyncBatch()
    {
        long now = TimeProvider.System.GetTimestamp();
        lock (Sync)
        {
            if (lastPartyBatchTimestamp != 0)
            {
                var interval = TimeProvider.System.GetElapsedTime(lastPartyBatchTimestamp, now);
                if (interval > TimeSpan.Zero && interval <= TimeSpan.FromMinutes(1))
                {
                    var measuredExpiry = TimeSpan.FromTicks(interval.Ticks * 3);
                    partyExpiry = measuredExpiry < MinimumPartyExpiry ? MinimumPartyExpiry : measuredExpiry;
                }
            }

            lastPartyBatchTimestamp = now;
        }
    }

    public static void ObservePartyMember(long uuid, uint memberSceneId, string? name = null, Vector3? position = null, long hp = 0, long maxHp = 0)
    {
        if (uuid == 0)
        {
            return;
        }

        var tracked = GetOrCreate(uuid);
        long now = TimeProvider.System.GetTimestamp();
        lock (Sync)
        {
            tracked.Sources |= EntitySource.PartySync;
            tracked.EntityType = EEntityType.EntChar;
            tracked.PartySceneId = memberSceneId;
            tracked.LastPartyTimestamp = now;
            if (!string.IsNullOrWhiteSpace(name))
            {
                tracked.Name = name;
            }

            if (position.HasValue)
            {
                tracked.PartyPosition = position.Value;
                tracked.HasPartyPosition = true;
            }

            if (maxHp > 0)
            {
                tracked.Hp = hp;
                tracked.MaxHp = maxHp;
                tracked.HasHp = true;
            }
        }
    }

    public static void RemovePartyMember(long uuid)
    {
        lock (Sync)
        {
            if (!Entities.TryGetValue(uuid, out var tracked))
            {
                return;
            }

            tracked.Sources &= ~EntitySource.PartySync;
            RemoveIfUnobserved(uuid, tracked);
        }
    }

    public static void ClearPartyMembers()
    {
        lock (Sync)
        {
            foreach (var pair in Entities.ToArray())
            {
                pair.Value.Sources &= ~EntitySource.PartySync;
                RemoveIfUnobserved(pair.Key, pair.Value);
            }

            lastPartyBatchTimestamp = 0;
            partyExpiry = MinimumPartyExpiry;
        }
    }

    public static string Describe(long uuid, IReadOnlyDictionary<long, ulong>? memAttrs = null, int memTypeUid = 0)
    {
        if (uuid == 0)
        {
            return "";
        }

        lock (Sync)
        {
            if (Entities.TryGetValue(uuid, out var entity) && !string.IsNullOrWhiteSpace(entity.Name))
            {
                return entity.Name;
            }
        }

        // Entity type uid read from game memory by the lock-target helper
        // (verified against the bundled tables), so first-lock names resolve
        // without any prior packet observation.
        if (memTypeUid != 0 && TryTableName(memTypeUid, out var typeName))
        {
            RecordName(uuid, typeName);
            return typeName;
        }

        // Attr records read from game memory: key 0x14C carries the entity's
        // table id (monster/dummy) for entities that expose an attr array.
        if (ResolveMemoryAttrName(uuid, memAttrs) is { } memName)
        {
            return memName;
        }

        if (PersistentNames.TryGetValue(uuid, out var cachedName))
        {
            return cachedName;
        }

        return $"#{UuidToEntityId(uuid)}";
    }

    // What the target overlay needs to emphasise an elite: the same
    // classification the radar draws with, looked up by uuid.
    // memTypeUid is the entity's table id as the lock-target helper read it
    // out of the game's memory. Without it this answers UnknownMonster for
    // exactly the targets that matter most: MonsterClass is filled in by the
    // packet path alone, and a first lock is routinely named from memory
    // before any packet has classified that entity -- the same reason
    // Describe takes it.
    public static RadarEntityKind ClassOf(long uuid, int memTypeUid = 0)
    {
        if (uuid == 0) return RadarEntityKind.UnknownMonster;
        lock (Sync)
        {
            if (Entities.TryGetValue(uuid, out var entity)
                && entity.MonsterClass != RadarEntityKind.UnknownMonster)
            {
                return entity.MonsterClass;
            }
        }
        if (memTypeUid != 0 && GameDataTables.Monsters.TryGetValue(memTypeUid, out var m) && !m.IsGimmick)
        {
            return m.MonsterType switch
            {
                1 => RadarEntityKind.Elite,
                2 => RadarEntityKind.Boss,
                _ => RadarEntityKind.Monster,
            };
        }
        return RadarEntityKind.UnknownMonster;
    }

    private const long MemAttrTableIdKey = 0x14C;

    private static string? ResolveMemoryAttrName(long uuid, IReadOnlyDictionary<long, ulong>? attrs)
    {
        if (attrs == null || attrs.Count == 0)
        {
            return null;
        }

        if (attrs.TryGetValue(MemAttrTableIdKey, out ulong tid) && tid <= int.MaxValue
            && TryTableName((int)tid, out var name))
        {
            RecordName(uuid, name);
            return name;
        }

        // Fallback: exactly one attr value matching a bundled table id.
        // Ambiguous or zero matches are ignored — values like HP can
        // coincidentally collide with table ids.
        string? found = null;
        int matches = 0;
        foreach (var v in attrs.Values)
        {
            if (v > 0 && v <= int.MaxValue && TryTableName((int)v, out var n))
            {
                matches++;
                found = n;
            }
        }
        if (matches == 1)
        {
            RecordName(uuid, found!);
            return found;
        }
        return null;
    }

    private static bool TryTableName(int id, out string name)
    {
        if (GameDataTables.Monsters.TryGetValue(id, out var m))
        {
            name = m.Name;
            return true;
        }
        if (GameDataTables.Dummys.TryGetValue(id, out var d))
        {
            name = d.Name;
            return true;
        }
        name = "";
        return false;
    }

    public static RadarSnapshot GetSnapshot(RadarSettings settings)
    {
        long now = TimeProvider.System.GetTimestamp();
        lock (Sync)
        {
            if (hasSnapshot && TimeProvider.System.GetElapsedTime(lastSnapshotTimestamp, now) < SnapshotInterval)
            {
                return cachedSnapshot;
            }

            ExpireSources(now);

            RadarEntitySnapshot? self = null;
            if (PlayerUuid != 0 && Entities.TryGetValue(PlayerUuid, out var selfEntity))
            {
                self = CreateSnapshot(selfEntity, RadarEntityKind.Self, now, null);
            }

            Vector3? selfPosition = self?.Position;
            var party = new List<RadarEntitySnapshot>();
            var players = new List<RadarEntitySnapshot>();
            var enemies = new List<RadarEntitySnapshot>();

            string[] partyIncludes = ParseTerms(settings.PartyNameIncludes);
            string[] partyExcludes = ParseTerms(settings.PartyNameExcludes);
            string[] enemyIncludes = ParseTerms(settings.EnemyNameIncludes);
            string[] enemyExcludes = ParseTerms(settings.EnemyNameExcludes);

            foreach (var tracked in Entities.Values)
            {
                var kind = Classify(tracked);
                if (kind == null || kind == RadarEntityKind.Self)
                {
                    continue;
                }

                var snapshot = CreateSnapshot(tracked, kind.Value, now, selfPosition);
                if (snapshot == null)
                {
                    continue;
                }

                if (kind == RadarEntityKind.Party)
                {
                    if (settings.ShowParty && NameAllowed(snapshot.Name, partyIncludes, partyExcludes))
                    {
                        party.Add(snapshot);
                    }
                    continue;
                }

                if (kind == RadarEntityKind.Player)
                {
                    if (settings.ShowPlayers)
                    {
                        players.Add(snapshot);
                    }
                    continue;
                }

                if (!KindEnabled(kind.Value, settings))
                {
                    continue;
                }

                float enemyMaxDistance = settings.EnemyMaxDistanceMeters;
                if (settings.DistanceCalibrationComplete)
                {
                    enemyMaxDistance *= MathF.Max(0.0001f, settings.WorldUnitsPerMeter);
                }
                if (enemyMaxDistance > 0 && selfPosition.HasValue && snapshot.DistanceFromSelf > enemyMaxDistance)
                {
                    continue;
                }

                if (NameAllowed(snapshot.Name, enemyIncludes, enemyExcludes))
                {
                    enemies.Add(snapshot);
                }
            }

            cachedSnapshot = new RadarSnapshot(
                sceneId,
                sceneName,
                self,
                party.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
                players.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
                enemies.OrderBy(x => x.Kind).ThenBy(x => x.DistanceFromSelf).ToArray());
            lastSnapshotTimestamp = now;
            hasSnapshot = true;
            return cachedSnapshot;
        }
    }

    private static TrackedEntity GetOrCreate(long uuid)
    {
        lock (Sync)
        {
            if (!Entities.TryGetValue(uuid, out var tracked))
            {
                tracked = new TrackedEntity { Uuid = uuid };
                Entities.Add(uuid, tracked);
            }
            return tracked;
        }
    }

    private static void RemoveIfUnobserved(long uuid, TrackedEntity tracked)
    {
        if (tracked.Sources == EntitySource.None)
        {
            Entities.Remove(uuid);
        }
    }

    private static void ExpireSources(long now)
    {
        foreach (var pair in Entities.ToArray())
        {
            var tracked = pair.Value;
            if (tracked.Sources.HasFlag(EntitySource.Aoi) && TimeProvider.System.GetElapsedTime(tracked.LastAoiTimestamp, now) >= AoiExpiry)
            {
                tracked.Sources &= ~EntitySource.Aoi;
            }

            if (tracked.Sources.HasFlag(EntitySource.PartySync) && TimeProvider.System.GetElapsedTime(tracked.LastPartyTimestamp, now) >= partyExpiry)
            {
                tracked.Sources &= ~EntitySource.PartySync;
            }

            RemoveIfUnobserved(pair.Key, pair.Value);
        }
    }

    private static RadarEntityKind? Classify(TrackedEntity tracked)
    {
        if (tracked.Uuid == PlayerUuid)
        {
            return RadarEntityKind.Self;
        }

        if (tracked.EntityType == EEntityType.EntChar)
        {
            return tracked.Sources.HasFlag(EntitySource.PartySync) ? RadarEntityKind.Party : RadarEntityKind.Player;
        }

        if (tracked.EntityType != EEntityType.EntMonster)
        {
            return null;
        }

        if (tracked.IsSummon)
        {
            return null;
        }

        if (tracked.HasCurrentHp && tracked.Hp <= 0)
        {
            return null;
        }

        return tracked.MonsterClass;
    }

    private static bool KindEnabled(RadarEntityKind kind, RadarSettings settings)
    {
        return kind switch
        {
            RadarEntityKind.Gimmick => settings.ShowGimmicks,
            RadarEntityKind.Monster => settings.ShowNormalMonsters,
            RadarEntityKind.Elite => settings.ShowEliteMonsters,
            RadarEntityKind.Boss => settings.ShowBosses,
            RadarEntityKind.UnknownMonster => settings.ShowUnknownMonsters,
            _ => false,
        };
    }

    private static RadarEntitySnapshot? CreateSnapshot(TrackedEntity tracked, RadarEntityKind kind, long now, Vector3? selfPosition)
    {
        if (!TryGetCurrentPosition(tracked, out var position, out var entitySceneId) || entitySceneId != sceneId)
        {
            return null;
        }

        float distance = selfPosition.HasValue ? PlanarDistance(selfPosition.Value, position) : 0;
        long lastSeen = Math.Max(tracked.LastAoiTimestamp, tracked.LastPartyTimestamp);
        var age = TimeProvider.System.GetElapsedTime(lastSeen, now);
        // Fading says "this position is probably out of date". For a player
        // that is true within seconds. For a monster it is not: an idle one
        // sends no update at all, and dimming it to a third within five
        // seconds hides exactly the enemies the player is trying to size up
        // before a pull.
        bool moves = kind is RadarEntityKind.Self or RadarEntityKind.Party or RadarEntityKind.Player;
        var delay = moves ? FadeDelay : IdleFadeDelay;
        double slope = moves ? 3d : 60d;
        float opacity = age <= delay
            ? 1f
            : Math.Clamp(1f - (float)((age - delay).TotalSeconds / slope), moves ? 0.35f : 0.6f, 1f);

        float? heading = null;
        if (kind == RadarEntityKind.Self)
        {
            lock (DirectionLock)
            {
                if (positionDirectionCount > 0)
                {
                    heading = lastPositionDirection * (MathF.PI / 180f);
                }
            }
        }

        return new RadarEntitySnapshot(
            tracked.Uuid,
            kind,
            tracked.Sources,
            position,
            heading,
            tracked.Name,
            tracked.Hp,
            tracked.MaxHp,
            tracked.HasHp,
            distance,
            opacity);
    }

    private static bool TryGetCurrentPosition(TrackedEntity tracked, out Vector3 position, out uint entitySceneId)
    {
        bool hasAoi = tracked.Sources.HasFlag(EntitySource.Aoi) && tracked.HasAoiPosition;
        bool hasParty = tracked.Sources.HasFlag(EntitySource.PartySync) && tracked.HasPartyPosition;

        if (hasAoi && (!hasParty || tracked.LastAoiTimestamp >= tracked.LastPartyTimestamp))
        {
            position = tracked.AoiPosition;
            entitySceneId = tracked.AoiSceneId;
            return true;
        }

        if (hasParty)
        {
            position = tracked.PartyPosition;
            entitySceneId = tracked.PartySceneId;
            return true;
        }

        position = default;
        entitySceneId = 0;
        return false;
    }

    private static float PlanarDistance(Vector3 left, Vector3 right)
    {
        float dx = left.X - right.X;
        float dz = left.Z - right.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    private static string[] ParseTerms(string value)
    {
        return (value ?? "")
            .Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool NameAllowed(string name, string[] includes, string[] excludes)
    {
        if (excludes.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return includes.Length == 0 || includes.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    public static long UuidToEntityId(long uuid) => uuid >> 16;
    public static long EntityIdToUuid(long uid, long entityType) => uid << 16 | (entityType << 6);
    public static bool IsSummonByUuid(long uuid) => ((uuid >> 15) & 1) == 1;

    // Offline harness: the set of entities the packet stream currently knows
    // about. Written next to each memory snapshot so a replay run can tell a
    // real lock-on uuid from a heap pointer that merely looks like one,
    // without the user labelling anything by hand.
    // Monsters the packet stream currently knows about. The mark harness
    // refuses to record where this is zero: it looks for addresses holding a
    // monster, so an empty area can only ever produce an empty result, and
    // two recordings were lost to exactly that before it was checked.
    public static int LiveMonsterCount()
    {
        int n = 0;
        lock (Sync)
        {
            foreach (var e in Entities.Values)
            {
                if (e.EntityType == EEntityType.EntMonster) n++;
            }
        }
        return n;
    }

    // A one-line census of what the packet side currently believes exists,
    // by EEntityType. This is the other half of the guild hall question: the
    // helper reported known=58/mon=0 and there was no way to see afterwards
    // what those 58 were, because entities.json is overwritten every second
    // and keeps no history.
    // Caller holds Sync.
    private static string CensusLocked()
    {
        var counts = new SortedDictionary<int, int>();
        int lockable = 0;
        foreach (var e in Entities.Values)
        {
            int t = (int)e.EntityType;
            counts[t] = counts.TryGetValue(t, out int n) ? n + 1 : 1;
            if (e.EntityType == EEntityType.EntMonster) lockable++;
        }
        return $"entities n={Entities.Count} mon={lockable} scene={sceneId} "
             + $"types=[{string.Join(" ", counts.Select(kv => $"{kv.Key}:{kv.Value}"))}]";
    }

    // Scene id and entity list in one lock. Sampling them separately let a
    // scene change land between the two, publishing the old scene id beside
    // the new scene's freshly cleared list -- which tells the helper the
    // world has not changed at the exact moment it has, defeating the field
    // that exists to tell it. Census is taken here too, so the line the log
    // gets describes the same instant.
    public static (uint Scene, List<EntityExportRecord> Entities) ExportLiveSnapshot()
    {
        string census;
        uint scene;
        List<EntityExportRecord> list;
        lock (Sync)
        {
            scene = sceneId;
            census = CensusLocked();
            list = ExportLocked();
        }
        TrackerLog.Composition(census);
        return (scene, list);
    }

    public static List<EntityExportRecord> ExportLiveEntities()
    {
        return ExportLiveSnapshot().Entities;
    }

    private static List<EntityExportRecord> ExportLocked()
    {
        var list = new List<EntityExportRecord>();
        {
            foreach (var e in Entities.Values)
            {
                list.Add(new EntityExportRecord
                {
                    Uuid = e.Uuid,
                    Name = e.Name ?? "",
                    EntityType = (int)e.EntityType,
                    TypeUid = e.TypeUid,
                    IsSummon = e.IsSummon,
                });
            }
        }
        return list;
    }

    public sealed class EntityExportRecord
    {
        public long Uuid { get; set; }
        public string Name { get; set; } = "";
        public int EntityType { get; set; }
        public int TypeUid { get; set; }
        public bool IsSummon { get; set; }
    }

    private sealed class TrackedEntity
    {
        public long Uuid { get; init; }
        public EntitySource Sources { get; set; }
        public EEntityType EntityType { get; set; }
        public RadarEntityKind MonsterClass { get; set; } = RadarEntityKind.UnknownMonster;
        public int TypeUid { get; set; }
        public bool IsSummon { get; set; }
        public Vector3 AoiPosition { get; set; }
        public Vector3 PartyPosition { get; set; }
        public bool HasAoiPosition { get; set; }
        public bool HasPartyPosition { get; set; }
        public uint AoiSceneId { get; set; }
        public uint PartySceneId { get; set; }
        public string Name { get; set; } = "";
        public long Hp { get; set; }
        public long MaxHp { get; set; }
        public bool HasHp { get; set; }
        public bool HasCurrentHp { get; set; }
        public long LastAoiTimestamp { get; set; }
        public long LastPartyTimestamp { get; set; }
    }
}
