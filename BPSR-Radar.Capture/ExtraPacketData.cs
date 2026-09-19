namespace BpsrRadar.Capture
{
    public class ExtraPacketData(DateTime arrivalTime)
    {
        public DateTime ArrivalTime { get; set; } = arrivalTime;
    }
}
