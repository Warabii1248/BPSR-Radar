namespace BpsrRadar.Capture;

public class NotifyId(ulong serviceId, uint methodId)
{
    public ulong ServiceId { get; set; } = serviceId;
    public uint MethodId { get; set; } = methodId;

    public override bool Equals(object? obj)
    {
        return ServiceId == ((NotifyId)obj).ServiceId && MethodId == ((NotifyId)obj).MethodId;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(ServiceId, MethodId);
    }
}