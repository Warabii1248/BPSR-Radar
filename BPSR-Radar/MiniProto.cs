using Google.Protobuf;
using Google.Protobuf.Collections;

// Minimal hand-written wire-format parsers covering only the message shapes
// the radar consumes. The generated protobuf sources upstream cannot be
// partially extracted (csharp.proto's FileDescriptor references every other
// .proto, pulling in ~1900 generated files), so this file replaces them.
// Field numbers mirror the upstream generated MergeFrom code.
namespace RadarProto;

public sealed class WireParser<T>
{
    private readonly Func<CodedInputStream, T> parse;

    internal WireParser(Func<CodedInputStream, T> parse) => this.parse = parse;

    public T ParseFrom(ReadOnlySpan<byte> data) => parse(new CodedInputStream(data.ToArray()));
    public T ParseFrom(ByteString data) => ParseFrom(data.Span);
    public T ParseFrom(byte[] data) => ParseFrom(data.AsSpan());
    public T ParseFrom(CodedInputStream input) => parse(input);
}

internal static class Wire
{
    public static T Msg<T>(CodedInputStream input, WireParser<T> parser)
        => parser.ParseFrom(input.ReadBytes().ToByteArray());
}

public enum EEntityType
{
    EntErrType = 0,
    EntMonster = 1,
    EntNpc = 2,
    EntSceneObject = 3,
    EntZone = 5,
    EntBullet = 6,
    EntClientBullet = 7,
    EntPet = 8,
    EntChar = 10,
    EntDummy = 11,
    EntDrop = 12,
    EntField = 14,
    EntTrap = 15,
    EntCollection = 16,
    EntStaticObject = 18,
    EntVehicle = 19,
    EntToy = 20,
    EntCommunityHouse = 21,
    EntHouseItem = 22,
    EntVanityPet = 23,
    EntCount = 24,
}

// Subset of EAttrType containing only the attributes the radar reads.
public enum EAttrType
{
    AttrName = 1,
    AttrId = 10,
    AttrPos = 52,
    AttrHp = 11310,
    AttrMaxHp = 11320,
}

public sealed class Attr
{
    public static WireParser<Attr> Parser { get; } = new(Parse);

    public int Id { get; set; }
    public ByteString RawData { get; set; } = ByteString.Empty;

    private static Attr Parse(CodedInputStream input)
    {
        var result = new Attr();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: result.Id = input.ReadInt32(); break;
                case 2: result.RawData = input.ReadBytes(); break;
                default: input.SkipLastField(); break;
            }
        }
        return result;
    }
}

public sealed class AttrCollection
{
    public static WireParser<AttrCollection> Parser { get; } = new(Parse);

    public RepeatedField<Attr> Attrs { get; } = new();

    private static AttrCollection Parse(CodedInputStream input)
    {
        var result = new AttrCollection();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 2: result.Attrs.Add(Wire.Msg(input, Attr.Parser)); break;
                default: input.SkipLastField(); break;
            }
        }
        return result;
    }
}

public sealed class Vec3
{
    public static WireParser<Vec3> Parser { get; } = new(Parse);

    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    private static Vec3 Parse(CodedInputStream input)
    {
        var result = new Vec3();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: result.X = input.ReadFloat(); break;
                case 2: result.Y = input.ReadFloat(); break;
                case 3: result.Z = input.ReadFloat(); break;
                default: input.SkipLastField(); break;
            }
        }
        return result;
    }
}

public sealed class Position
{
    public static WireParser<Position> Parser { get; } = new(Parse);

    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Dir { get; set; }

    private static Position Parse(CodedInputStream input)
    {
        var result = new Position();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: result.X = input.ReadFloat(); break;
                case 2: result.Y = input.ReadFloat(); break;
                case 3: result.Z = input.ReadFloat(); break;
                case 4: result.Dir = input.ReadFloat(); break;
                default: input.SkipLastField(); break;
            }
        }
        return result;
    }
}

public sealed class Entity
{
    public static WireParser<Entity> Parser { get; } = new(Parse);

    public long Uuid { get; set; }
    public EEntityType EntType { get; set; }
    public AttrCollection? Attrs { get; set; }

    private static Entity Parse(CodedInputStream input)
    {
        var result = new Entity();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: result.Uuid = input.ReadInt64(); break;
                case 2: result.EntType = (EEntityType)input.ReadEnum(); break;
                case 3: result.Attrs = Wire.Msg(input, AttrCollection.Parser); break;
                default: input.SkipLastField(); break;
            }
        }
        return result;
    }
}

public sealed class DisappearEntity
{
    public static WireParser<DisappearEntity> Parser { get; } = new(Parse);

    public long Uuid { get; set; }

    private static DisappearEntity Parse(CodedInputStream input)
    {
        var result = new DisappearEntity();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: result.Uuid = input.ReadInt64(); break;
                default: input.SkipLastField(); break;
            }
        }
        return result;
    }
}

public sealed class AoiSyncDelta
{
    public static WireParser<AoiSyncDelta> Parser { get; } = new(Parse);

    public long Uuid { get; set; }
    public AttrCollection? Attrs { get; set; }

    private static AoiSyncDelta Parse(CodedInputStream input)
    {
        var result = new AoiSyncDelta();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: result.Uuid = input.ReadInt64(); break;
                case 2: result.Attrs = Wire.Msg(input, AttrCollection.Parser); break;
                default: input.SkipLastField(); break;
            }
        }
        return result;
    }
}

public sealed class AoiSyncToMeDelta
{
    public static WireParser<AoiSyncToMeDelta> Parser { get; } = new(Parse);

    public AoiSyncDelta? BaseDelta { get; set; }
    public long Uuid { get; set; }

    private static AoiSyncToMeDelta Parse(CodedInputStream input)
    {
        var result = new AoiSyncToMeDelta();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: result.BaseDelta = Wire.Msg(input, AoiSyncDelta.Parser); break;
                case 5: result.Uuid = input.ReadInt64(); break;
                default: input.SkipLastField(); break;
            }
        }
        return result;
    }
}

public sealed class EnterSceneInfo
{
    public static WireParser<EnterSceneInfo> Parser { get; } = new(Parse);

    public Entity? PlayerEnt { get; set; }

    private static EnterSceneInfo Parse(CodedInputStream input)
    {
        var result = new EnterSceneInfo();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 2: result.PlayerEnt = Wire.Msg(input, Entity.Parser); break;
                default: input.SkipLastField(); break;
            }
        }
        return result;
    }
}

public sealed class CharBaseInfo
{
    public static WireParser<CharBaseInfo> Parser { get; } = new(Parse);

    public string Name { get; set; } = "";

    private static CharBaseInfo Parse(CodedInputStream input)
    {
        var result = new CharBaseInfo();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 5: result.Name = input.ReadString(); break;
                default: input.SkipLastField(); break;
            }
        }
        return result;
    }
}

public sealed class SceneData
{
    public static WireParser<SceneData> Parser { get; } = new(Parse);

    public uint LevelMapId { get; set; }

    private static SceneData Parse(CodedInputStream input)
    {
        var result = new SceneData();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 6: result.LevelMapId = input.ReadUInt32(); break;
                default: input.SkipLastField(); break;
            }
        }
        return result;
    }
}

public sealed class CharSerialize
{
    public static WireParser<CharSerialize> Parser { get; } = new(Parse);

    public long CharId { get; set; }
    public CharBaseInfo? CharBase { get; set; }
    public SceneData? SceneData { get; set; }

    private static CharSerialize Parse(CodedInputStream input)
    {
        var result = new CharSerialize();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: result.CharId = input.ReadInt64(); break;
                case 2: result.CharBase = Wire.Msg(input, CharBaseInfo.Parser); break;
                case 3: result.SceneData = Wire.Msg(input, SceneData.Parser); break;
                default: input.SkipLastField(); break;
            }
        }
        return result;
    }
}

public sealed class BasicData
{
    public static WireParser<BasicData> Parser { get; } = new(Parse);

    public string Name { get; set; } = "";

    private static BasicData Parse(CodedInputStream input)
    {
        var result = new BasicData();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 3: result.Name = input.ReadString(); break;
                default: input.SkipLastField(); break;
            }
        }
        return result;
    }
}

public sealed class SocialData
{
    public static WireParser<SocialData> Parser { get; } = new(Parse);

    public BasicData? BasicData { get; set; }
    public SceneData? SceneData { get; set; }

    private static SocialData Parse(CodedInputStream input)
    {
        var result = new SocialData();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 3: result.BasicData = Wire.Msg(input, BasicData.Parser); break;
                case 10: result.SceneData = Wire.Msg(input, SceneData.Parser); break;
                default: input.SkipLastField(); break;
            }
        }
        return result;
    }
}

public sealed class TeamMemberSocialData
{
    public static WireParser<TeamMemberSocialData> Parser { get; } = new(Parse);

    public BasicData? BasicData { get; set; }

    private static TeamMemberSocialData Parse(CodedInputStream input)
    {
        var result = new TeamMemberSocialData();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: result.BasicData = Wire.Msg(input, BasicData.Parser); break;
                default: input.SkipLastField(); break;
            }
        }
        return result;
    }
}

public sealed class TeamMemData
{
    public static WireParser<TeamMemData> Parser { get; } = new(Parse);

    public long CharId { get; set; }
    public int SceneId { get; set; }
    public TeamMemberSocialData? SocialData { get; set; }

    private static TeamMemData Parse(CodedInputStream input)
    {
        var result = new TeamMemData();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: result.CharId = input.ReadInt64(); break;
                case 6: result.SceneId = input.ReadInt32(); break;
                case 9: result.SocialData = Wire.Msg(input, TeamMemberSocialData.Parser); break;
                default: input.SkipLastField(); break;
            }
        }
        return result;
    }
}

public sealed class TeamMemberFastSyncData
{
    public static WireParser<TeamMemberFastSyncData> Parser { get; } = new(Parse);

    public long CharId { get; set; }
    public int SceneId { get; set; }
    public Position? Position { get; set; }
    public long Hp { get; set; }
    public long MaxHp { get; set; }

    private static TeamMemberFastSyncData Parse(CodedInputStream input)
    {
        var result = new TeamMemberFastSyncData();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: result.CharId = input.ReadInt64(); break;
                case 2: result.SceneId = input.ReadInt32(); break;
                case 3: result.Position = Wire.Msg(input, Position.Parser); break;
                case 4: result.Hp = input.ReadInt64(); break;
                case 5: result.MaxHp = input.ReadInt64(); break;
                default: input.SkipLastField(); break;
            }
        }
        return result;
    }
}

public sealed class NotifySocialDataRequest
{
    public static WireParser<NotifySocialDataRequest> Parser { get; } = new(Parse);

    public SocialData? Data { get; set; }

    private static NotifySocialDataRequest Parse(CodedInputStream input)
    {
        var result = new NotifySocialDataRequest();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: result.Data = Wire.Msg(input, SocialData.Parser); break;
                default: input.SkipLastField(); break;
            }
        }
        return result;
    }
}

public sealed class NoticeUpdateTeamMemberInfoRequest
{
    public static WireParser<NoticeUpdateTeamMemberInfoRequest> Parser { get; } = new(Parse);

    public RepeatedField<TeamMemberFastSyncData> TeamMemberSyncDatas { get; } = new();
    public RepeatedField<TeamMemData> TeamMemberSocialDatas { get; } = new();

    private static NoticeUpdateTeamMemberInfoRequest Parse(CodedInputStream input)
    {
        var result = new NoticeUpdateTeamMemberInfoRequest();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 5: result.TeamMemberSyncDatas.Add(Wire.Msg(input, TeamMemberFastSyncData.Parser)); break;
                case 6: result.TeamMemberSocialDatas.Add(Wire.Msg(input, TeamMemData.Parser)); break;
                default: input.SkipLastField(); break;
            }
        }
        return result;
    }
}

public sealed class NotifyJoinTeamRequest
{
    public static WireParser<NotifyJoinTeamRequest> Parser { get; } = new(Parse);

    public RepeatedField<TeamMemData> MemberData { get; } = new();

    private static NotifyJoinTeamRequest Parse(CodedInputStream input)
    {
        var result = new NotifyJoinTeamRequest();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 2: result.MemberData.Add(Wire.Msg(input, TeamMemData.Parser)); break;
                default: input.SkipLastField(); break;
            }
        }
        return result;
    }
}

public sealed class NotifyLeaveTeamRequest
{
    public static WireParser<NotifyLeaveTeamRequest> Parser { get; } = new(Parse);

    public long CharId { get; set; }

    private static NotifyLeaveTeamRequest Parse(CodedInputStream input)
    {
        var result = new NotifyLeaveTeamRequest();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: result.CharId = input.ReadInt64(); break;
                default: input.SkipLastField(); break;
            }
        }
        return result;
    }
}

public sealed class UserControlInfo
{
    public static WireParser<UserControlInfo> Parser { get; } = new(Parse);

    public Position? CurPos { get; set; }
    public Position? DestPos { get; set; }

    private static UserControlInfo Parse(CodedInputStream input)
    {
        var result = new UserControlInfo();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 12: result.CurPos = Wire.Msg(input, Position.Parser); break;
                case 13: result.DestPos = Wire.Msg(input, Position.Parser); break;
                default: input.SkipLastField(); break;
            }
        }
        return result;
    }
}

public static class WorldNtfCsharp
{
    public static class Types
    {
        public sealed class EnterScene
        {
            public static WireParser<EnterScene> Parser { get; } = new(Parse);

            public EnterSceneInfo? EnterSceneInfo { get; set; }

            private static EnterScene Parse(CodedInputStream input)
            {
                var result = new EnterScene();
                uint tag;
                while ((tag = input.ReadTag()) != 0)
                {
                    switch (WireFormat.GetTagFieldNumber(tag))
                    {
                        case 1: result.EnterSceneInfo = Wire.Msg(input, EnterSceneInfo.Parser); break;
                        default: input.SkipLastField(); break;
                    }
                }
                return result;
            }
        }

        public sealed class SyncContainerData
        {
            public static WireParser<SyncContainerData> Parser { get; } = new(Parse);

            public CharSerialize? VData { get; set; }

            private static SyncContainerData Parse(CodedInputStream input)
            {
                var result = new SyncContainerData();
                uint tag;
                while ((tag = input.ReadTag()) != 0)
                {
                    switch (WireFormat.GetTagFieldNumber(tag))
                    {
                        case 1: result.VData = Wire.Msg(input, CharSerialize.Parser); break;
                        default: input.SkipLastField(); break;
                    }
                }
                return result;
            }
        }

        public sealed class SyncNearEntities
        {
            public static WireParser<SyncNearEntities> Parser { get; } = new(Parse);

            public RepeatedField<Entity> Appear { get; } = new();
            public RepeatedField<DisappearEntity> Disappear { get; } = new();

            private static SyncNearEntities Parse(CodedInputStream input)
            {
                var result = new SyncNearEntities();
                uint tag;
                while ((tag = input.ReadTag()) != 0)
                {
                    switch (WireFormat.GetTagFieldNumber(tag))
                    {
                        case 1: result.Appear.Add(Wire.Msg(input, Entity.Parser)); break;
                        case 2: result.Disappear.Add(Wire.Msg(input, DisappearEntity.Parser)); break;
                        default: input.SkipLastField(); break;
                    }
                }
                return result;
            }
        }

        public sealed class SyncNearDeltaInfo
        {
            public static WireParser<SyncNearDeltaInfo> Parser { get; } = new(Parse);

            public RepeatedField<AoiSyncDelta> DeltaInfos { get; } = new();

            private static SyncNearDeltaInfo Parse(CodedInputStream input)
            {
                var result = new SyncNearDeltaInfo();
                uint tag;
                while ((tag = input.ReadTag()) != 0)
                {
                    switch (WireFormat.GetTagFieldNumber(tag))
                    {
                        case 1: result.DeltaInfos.Add(Wire.Msg(input, AoiSyncDelta.Parser)); break;
                        default: input.SkipLastField(); break;
                    }
                }
                return result;
            }
        }

        public sealed class SyncToMeDeltaInfo
        {
            public static WireParser<SyncToMeDeltaInfo> Parser { get; } = new(Parse);

            public AoiSyncToMeDelta? DeltaInfo { get; set; }

            private static SyncToMeDeltaInfo Parse(CodedInputStream input)
            {
                var result = new SyncToMeDeltaInfo();
                uint tag;
                while ((tag = input.ReadTag()) != 0)
                {
                    switch (WireFormat.GetTagFieldNumber(tag))
                    {
                        case 1: result.DeltaInfo = Wire.Msg(input, AoiSyncToMeDelta.Parser); break;
                        default: input.SkipLastField(); break;
                    }
                }
                return result;
            }
        }
    }
}

public static class WorldCsharp
{
    public static class Types
    {
        public sealed class NewMove
        {
            public static WireParser<NewMove> Parser { get; } = new(Parse);

            public UserControlInfo? Info { get; set; }

            private static NewMove Parse(CodedInputStream input)
            {
                var result = new NewMove();
                uint tag;
                while ((tag = input.ReadTag()) != 0)
                {
                    switch (WireFormat.GetTagFieldNumber(tag))
                    {
                        case 1: result.Info = Wire.Msg(input, UserControlInfo.Parser); break;
                        default: input.SkipLastField(); break;
                    }
                }
                return result;
            }
        }
    }
}

public static class SocialNtf
{
    public static class Types
    {
        public sealed class NotifySocialData
        {
            public static WireParser<NotifySocialData> Parser { get; } = new(Parse);

            public NotifySocialDataRequest? VRequest { get; set; }

            private static NotifySocialData Parse(CodedInputStream input)
            {
                var result = new NotifySocialData();
                uint tag;
                while ((tag = input.ReadTag()) != 0)
                {
                    switch (WireFormat.GetTagFieldNumber(tag))
                    {
                        case 1: result.VRequest = Wire.Msg(input, NotifySocialDataRequest.Parser); break;
                        default: input.SkipLastField(); break;
                    }
                }
                return result;
            }
        }
    }
}

public static class GrpcTeamNtf
{
    public static class Types
    {
        public sealed class NoticeUpdateTeamMemberInfo
        {
            public static WireParser<NoticeUpdateTeamMemberInfo> Parser { get; } = new(Parse);

            public NoticeUpdateTeamMemberInfoRequest? VRequest { get; set; }

            private static NoticeUpdateTeamMemberInfo Parse(CodedInputStream input)
            {
                var result = new NoticeUpdateTeamMemberInfo();
                uint tag;
                while ((tag = input.ReadTag()) != 0)
                {
                    switch (WireFormat.GetTagFieldNumber(tag))
                    {
                        case 1: result.VRequest = Wire.Msg(input, NoticeUpdateTeamMemberInfoRequest.Parser); break;
                        default: input.SkipLastField(); break;
                    }
                }
                return result;
            }
        }

        public sealed class NotifyJoinTeam
        {
            public static WireParser<NotifyJoinTeam> Parser { get; } = new(Parse);

            public NotifyJoinTeamRequest? VRequest { get; set; }

            private static NotifyJoinTeam Parse(CodedInputStream input)
            {
                var result = new NotifyJoinTeam();
                uint tag;
                while ((tag = input.ReadTag()) != 0)
                {
                    switch (WireFormat.GetTagFieldNumber(tag))
                    {
                        case 1: result.VRequest = Wire.Msg(input, NotifyJoinTeamRequest.Parser); break;
                        default: input.SkipLastField(); break;
                    }
                }
                return result;
            }
        }

        public sealed class NotifyLeaveTeam
        {
            public static WireParser<NotifyLeaveTeam> Parser { get; } = new(Parse);

            public NotifyLeaveTeamRequest? VRequest { get; set; }

            private static NotifyLeaveTeam Parse(CodedInputStream input)
            {
                var result = new NotifyLeaveTeam();
                uint tag;
                while ((tag = input.ReadTag()) != 0)
                {
                    switch (WireFormat.GetTagFieldNumber(tag))
                    {
                        case 1: result.VRequest = Wire.Msg(input, NotifyLeaveTeamRequest.Parser); break;
                        default: input.SkipLastField(); break;
                    }
                }
                return result;
            }
        }
    }
}
