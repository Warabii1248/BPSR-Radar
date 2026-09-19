namespace BpsrRadar.Capture;

public class NetCapConfig
{
    public string CaptureDeviceName { get; set; } = string.Empty;
    public string[] ExeNames { get; set; } = ["BPSR", "BPSR_STEAM"];
    public TimeSpan ConnectionScanInterval { get; set; } = TimeSpan.FromSeconds(10);

    // Offline harness. RecordPcapPath: also write every captured packet to
    // this pcap file. ReplayPcapPath: read packets from this file instead of
    // a live device (no capture device, no game, no admin needed).
    // ReplayRealtime: reproduce the original inter-packet delays; false
    // replays as fast as possible.
    public string? RecordPcapPath { get; set; }
    public string? ReplayPcapPath { get; set; }
    public bool ReplayRealtime { get; set; }
}