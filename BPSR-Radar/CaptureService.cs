using BpsrRadar.Capture;
using Serilog;
using SharpPcap.LibPcap;
using System.Numerics;
using RadarProto;
using static RadarProto.WorldNtfCsharp.Types;

namespace BpsrRadar;

// Minimal port of the packet pipeline pieces the radar needs:
// AOI appear/disappear/delta, self entity, scene id, party fast sync,
// and the self-heading movement proxy.
internal static class CaptureService
{
    private static NetCap? netCap;

    public static string CaptureDeviceName { get; private set; } = "";
    public static bool Running { get; private set; }
    public static string? LastError { get; private set; }

    // Offline harness switches, set from the command line before Start().
    public static string? RecordPcapPath;
    public static string? ReplayPcapPath;
    public static bool ReplayRealtime;

    public static void Start()
    {
        try
        {
            var settings = RadarSettings.Instance;
            string deviceName = !string.IsNullOrWhiteSpace(settings.CaptureDeviceName)
                ? settings.CaptureDeviceName
                : (TryFindBestNetworkDevice()?.Name ?? "");

            netCap = new NetCap();
            netCap.RawPacketObserver = PacketDiag.Observe;
            netCap.Init(new NetCapConfig
            {
                CaptureDeviceName = deviceName,
                ExeNames = settings.GameExeNames,
                RecordPcapPath = RecordPcapPath,
                ReplayPcapPath = ReplayPcapPath,
                ReplayRealtime = ReplayRealtime,
            });

            netCap.RegisterWorldNotifyHandler(BpsrRadar.Capture.ServiceMethods.WorldNtf.EnterScene, ProcessEnterScene);
            netCap.RegisterWorldNotifyHandler(BpsrRadar.Capture.ServiceMethods.WorldNtf.SyncContainerData, ProcessSyncContainerData);
            netCap.RegisterWorldNotifyHandler(BpsrRadar.Capture.ServiceMethods.WorldNtf.SyncNearDeltaInfo, ProcessSyncNearDeltaInfo);
            netCap.RegisterWorldNotifyHandler(BpsrRadar.Capture.ServiceMethods.WorldNtf.SyncToMeDeltaInfo, ProcessSyncToMeDeltaInfo);
            netCap.RegisterWorldNotifyHandler(BpsrRadar.Capture.ServiceMethods.WorldNtf.SyncNearEntities, ProcessSyncNearEntities);

            netCap.RegisterNotifyHandler((ulong)EServiceId.SocialNtf, (uint)BpsrRadar.Capture.ServiceMethods.SocialNtf.NotifySocialData, ProcessNotifySocialData);

            netCap.RegisterNotifyHandler((ulong)EServiceId.GrpcTeamNtf, (uint)BpsrRadar.Capture.ServiceMethods.GrpcTeamNtf.NoticeUpdateTeamMemberInfo, ProcessNoticeUpdateTeamMemberInfo);
            netCap.RegisterNotifyHandler((ulong)EServiceId.GrpcTeamNtf, (uint)BpsrRadar.Capture.ServiceMethods.GrpcTeamNtf.NotifyJoinTeam, ProcessNotifyJoinTeam);
            netCap.RegisterNotifyHandler((ulong)EServiceId.GrpcTeamNtf, (uint)BpsrRadar.Capture.ServiceMethods.GrpcTeamNtf.NotifyLeaveTeam, ProcessNotifyLeaveTeam);
            netCap.RegisterNotifyHandler((ulong)EServiceId.GrpcTeamNtf, (uint)BpsrRadar.Capture.ServiceMethods.GrpcTeamNtf.NoticeTeamDissolve, ProcessNoticeTeamDissolve);

            netCap.RegisterProxyHandler((uint)EProxyServiceId.World, (uint)BpsrRadar.Capture.ServiceMethods.WorldProxy.GetUserControlInfo, ProcessUserControlInfo);

            // Lock-on target via packets: WorldActivityNtf ClientTargetChange
            // (0x300B) carries TargetUuid at field 4. Observed on both the
            // server->client notify and the client->server proxy path, so
            // register both. Packet-derived targets are instant and need no
            // memory scan.
            netCap.RegisterNotifyHandler((ulong)EServiceId.WorldActivityNtf,
                (uint)BpsrRadar.Capture.ServiceMethods.WorldActivityNtf.ClientTargetChange,
                ProcessClientTargetChange);
            netCap.RegisterProxyHandler((uint)EProxyServiceId.World,
                (uint)BpsrRadar.Capture.ServiceMethods.WorldActivityNtf.ClientTargetChange,
                ProcessClientTargetChangeProxy);

            netCap.Start();
            CaptureDeviceName = deviceName;
            Running = true;
            LastError = null;
        }
        catch (Exception ex)
        {
            Running = false;
            LastError = ex.Message;
            Log.Error(ex, "Capture start failed");
        }
    }

    public static void Stop()
    {
        try
        {
            netCap?.Stop();
        }
        catch
        {
        }
        Running = false;
    }

    public static LibPcapLiveDevice? TryFindBestNetworkDevice()
    {
        var devices = LibPcapLiveDeviceList.Instance;
        foreach (var device in devices)
        {
            if (device.Addresses.Count == 0)
            {
                continue;
            }
            if (device.Interface?.GatewayAddresses.Count == 0)
            {
                continue;
            }
            if (device.MacAddress == null)
            {
                continue;
            }
            return device;
        }
        return null;
    }

    private static void ProcessEnterScene(ReadOnlySpan<byte> payloadBuffer, ExtraPacketData extraData)
    {
        if (payloadBuffer.Length == 0)
        {
            return;
        }

        var vData = EnterScene.Parser.ParseFrom(payloadBuffer);
        var playerEnt = vData.EnterSceneInfo?.PlayerEnt;
        if (playerEnt?.Attrs?.Attrs != null)
        {
            RadarTracker.ProcessAttrs(playerEnt.Uuid, playerEnt.Attrs.Attrs);
        }
    }

    private static void ProcessSyncContainerData(ReadOnlySpan<byte> payloadBuffer, ExtraPacketData extraData)
    {
        if (payloadBuffer.Length == 0)
        {
            return;
        }

        var vData = SyncContainerData.Parser.ParseFrom(payloadBuffer)?.VData;
        if (vData?.CharId == null || vData.CharId == 0)
        {
            return;
        }

        long playerUuid = RadarTracker.EntityIdToUuid(vData.CharId, (long)EEntityType.EntChar);
        RadarTracker.SetSelf(playerUuid);

        if (!string.IsNullOrEmpty(vData.CharBase?.Name))
        {
            RadarTracker.SetEntityName(playerUuid, vData.CharBase.Name);
        }

        if (vData.SceneData != null)
        {
            RadarTracker.SetScene(vData.SceneData.LevelMapId);
        }
    }

    private static void ProcessNotifySocialData(ReadOnlySpan<byte> payloadBuffer, ExtraPacketData extraData)
    {
        if (payloadBuffer.Length == 0)
        {
            return;
        }

        var vData = SocialNtf.Types.NotifySocialData.Parser.ParseFrom(payloadBuffer);
        if (vData?.VRequest?.Data?.SceneData != null)
        {
            RadarTracker.SetScene(vData.VRequest.Data.SceneData.LevelMapId);
        }
    }

    private static void ProcessSyncNearEntities(ReadOnlySpan<byte> payloadBuffer, ExtraPacketData extraData)
    {
        var syncNearEntities = SyncNearEntities.Parser.ParseFrom(payloadBuffer);
        if (syncNearEntities.Disappear != null)
        {
            foreach (var entity in syncNearEntities.Disappear)
            {
                RadarTracker.RemoveAoi(entity.Uuid);
            }
        }

        if (syncNearEntities.Appear != null)
        {
            foreach (var entity in syncNearEntities.Appear)
            {
                long uid = RadarTracker.UuidToEntityId(entity.Uuid);
                if (uid == 0)
                {
                    continue;
                }

                RadarTracker.SetEntityType(entity.Uuid, entity.EntType);
                if (entity.Attrs?.Attrs != null)
                {
                    RadarTracker.ProcessAttrs(entity.Uuid, entity.Attrs.Attrs);
                }

                RadarTracker.ObserveAoi(entity.Uuid);
            }
        }
    }

    private static void ProcessSyncNearDeltaInfo(ReadOnlySpan<byte> payloadBuffer, ExtraPacketData extraData)
    {
        var syncNearDeltaInfo = SyncNearDeltaInfo.Parser.ParseFrom(payloadBuffer);
        if (syncNearDeltaInfo.DeltaInfos == null || syncNearDeltaInfo.DeltaInfos.Count == 0)
        {
            return;
        }

        foreach (var aoiSyncDelta in syncNearDeltaInfo.DeltaInfos)
        {
            ProcessAoiSyncDelta(aoiSyncDelta);
        }
    }

    private static void ProcessSyncToMeDeltaInfo(ReadOnlySpan<byte> payloadBuffer, ExtraPacketData extraData)
    {
        var syncToMeDeltaInfo = SyncToMeDeltaInfo.Parser.ParseFrom(payloadBuffer);
        var aoiSyncToMeDelta = syncToMeDeltaInfo.DeltaInfo;
        if (aoiSyncToMeDelta == null)
        {
            return;
        }
        long uuid = aoiSyncToMeDelta.Uuid;
        if (uuid != 0 && RadarTracker.PlayerUuid != uuid)
        {
            RadarTracker.SetSelf(uuid);
        }

        if (aoiSyncToMeDelta.BaseDelta != null)
        {
            ProcessAoiSyncDelta(aoiSyncToMeDelta.BaseDelta);
        }
    }

    private static void ProcessAoiSyncDelta(AoiSyncDelta delta)
    {
        if (delta == null || delta.Uuid == 0)
        {
            return;
        }

        if (delta.Attrs?.Attrs != null && delta.Attrs.Attrs.Count > 0)
        {
            RadarTracker.ProcessAttrs(delta.Uuid, delta.Attrs.Attrs);
        }

        RadarTracker.ObserveAoi(delta.Uuid);
    }

    private static void ProcessClientTargetChange(ReadOnlySpan<byte> payloadBuffer, ExtraPacketData extraData)
    {
        bool ok = TryReadVarintField(payloadBuffer, 4, out ulong targetUuid);
        PacketDiag.Note($"ClientTargetChange len={payloadBuffer.Length} f4ok={ok} hex={Convert.ToHexString(payloadBuffer[..Math.Min(48, payloadBuffer.Length)])}");
        if (ok)
        {
            LockTargetService.OnPacketTargetUuid(unchecked((long)targetUuid));
        }
    }

    private static void ProcessClientTargetChangeProxy(ReadOnlySpan<byte> payloadBuffer, uint returnUid, ExtraPacketData extraData)
    {
        ProcessClientTargetChange(payloadBuffer, extraData);
    }

    // Reads protobuf field `fieldNumber` as a varint without a generated
    // message (wire types 0/1/2/5 only). Returns false if the field is absent.
    private static bool TryReadVarintField(ReadOnlySpan<byte> data, int fieldNumber, out ulong value)
    {
        value = 0;
        int offset = 0;
        while (offset < data.Length)
        {
            if (!TryReadVarint(data, ref offset, out ulong tag))
            {
                return false;
            }

            int field = (int)(tag >> 3);
            int wire = (int)(tag & 7);
            if (wire == 0)
            {
                if (!TryReadVarint(data, ref offset, out ulong fieldValue))
                {
                    return false;
                }

                if (field == fieldNumber)
                {
                    value = fieldValue;
                    return true;
                }
            }
            else if (wire == 2)
            {
                if (!TryReadVarint(data, ref offset, out ulong length) || length > (ulong)(data.Length - offset))
                {
                    return false;
                }

                offset += (int)length;
            }
            else if (wire == 5)
            {
                offset += 4;
            }
            else if (wire == 1)
            {
                offset += 8;
            }
            else
            {
                return false;
            }
        }

        return false;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int offset, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (offset < data.Length && shift < 70)
        {
            byte b = data[offset++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
        }

        return false;
    }

    private static void ProcessUserControlInfo(ReadOnlySpan<byte> payloadBuffer, uint returnUid, ExtraPacketData extraData)
    {
        try
        {
            var data = WorldCsharp.Types.NewMove.Parser.ParseFrom(payloadBuffer);
            var position = data.Info?.CurPos ?? data.Info?.DestPos;
            if (position == null || !float.IsFinite(position.Dir))
            {
                return;
            }

            RadarTracker.NoteDirection(position.Dir);
        }
        catch (Google.Protobuf.InvalidProtocolBufferException)
        {
        }
    }

    private static long ToCharacterUuid(long charId)
    {
        return charId == 0 ? 0 : RadarTracker.EntityIdToUuid(charId, (long)EEntityType.EntChar);
    }

    private static uint ToSceneId(int sceneId)
    {
        return sceneId > 0 ? (uint)sceneId : 0;
    }

    private static void UpdateMemberName(TeamMemData member)
    {
        long uuid = ToCharacterUuid(member.CharId);
        string? name = member.SocialData?.BasicData?.Name;
        if (uuid != 0 && !string.IsNullOrEmpty(name))
        {
            RadarTracker.SetEntityName(uuid, name);
        }
    }

    private static void ProcessNoticeUpdateTeamMemberInfo(ReadOnlySpan<byte> payloadBuffer, ExtraPacketData extraData)
    {
        if (payloadBuffer.Length == 0)
        {
            return;
        }

        var vData = GrpcTeamNtf.Types.NoticeUpdateTeamMemberInfo.Parser.ParseFrom(payloadBuffer);
        if (vData?.VRequest == null)
        {
            return;
        }

        foreach (var member in vData.VRequest.TeamMemberSocialDatas)
        {
            UpdateMemberName(member);
            long uuid = ToCharacterUuid(member.CharId);
            RadarTracker.ObservePartyMember(uuid, ToSceneId(member.SceneId), member.SocialData?.BasicData?.Name);
        }

        if (vData.VRequest.TeamMemberSyncDatas.Count > 0)
        {
            RadarTracker.BeginPartySyncBatch();
        }
        foreach (var member in vData.VRequest.TeamMemberSyncDatas)
        {
            long uuid = ToCharacterUuid(member.CharId);
            Vector3? position = member.Position == null
                ? null
                : new Vector3(member.Position.X, member.Position.Y, member.Position.Z);
            RadarTracker.ObservePartyMember(uuid, ToSceneId(member.SceneId), null, position, member.Hp, member.MaxHp);
        }
    }

    private static void ProcessNotifyJoinTeam(ReadOnlySpan<byte> payloadBuffer, ExtraPacketData extraData)
    {
        if (payloadBuffer.Length == 0)
        {
            return;
        }

        var vData = GrpcTeamNtf.Types.NotifyJoinTeam.Parser.ParseFrom(payloadBuffer);
        if (vData?.VRequest == null)
        {
            return;
        }

        foreach (var member in vData.VRequest.MemberData)
        {
            UpdateMemberName(member);
            RadarTracker.ObservePartyMember(ToCharacterUuid(member.CharId), ToSceneId(member.SceneId), member.SocialData?.BasicData?.Name);
        }
    }

    private static void ProcessNotifyLeaveTeam(ReadOnlySpan<byte> payloadBuffer, ExtraPacketData extraData)
    {
        if (payloadBuffer.Length == 0)
        {
            return;
        }

        var vData = GrpcTeamNtf.Types.NotifyLeaveTeam.Parser.ParseFrom(payloadBuffer);
        if (vData?.VRequest == null)
        {
            return;
        }

        if (vData.VRequest.CharId == RadarTracker.PlayerUid)
        {
            RadarTracker.ClearPartyMembers();
        }
        else
        {
            RadarTracker.RemovePartyMember(ToCharacterUuid(vData.VRequest.CharId));
        }
    }

    private static void ProcessNoticeTeamDissolve(ReadOnlySpan<byte> payloadBuffer, ExtraPacketData extraData)
    {
        if (payloadBuffer.Length == 0)
        {
            return;
        }

        RadarTracker.ClearPartyMembers();
    }
}
