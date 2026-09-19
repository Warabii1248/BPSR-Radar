using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BpsrRadar;

public enum RadarViewMode
{
    MiniMap,
    ScanOverview,
}

public enum RadarLabelMode
{
    None,
    PartyAndBoss,
    All,
}

public sealed class RadarSettings
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BPSR-Radar");
    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    public static RadarSettings Instance { get; private set; } = new();

    // UI language: "en" | "ja"
    public string Language { get; set; } = "en";

    // Window
    public double WindowLeft { get; set; }
    public double WindowTop { get; set; }
    public double WindowWidth { get; set; } = 340;
    public double WindowHeight { get; set; } = 420;
    public bool HasWindowPosition { get; set; }
    public bool TopMost { get; set; } = true;
    public bool ClickThrough { get; set; }

    // Capture
    public string CaptureDeviceName { get; set; } = "";
    public string[] GameExeNames { get; set; } =
        ["BPSR", "BPSR_STEAM", "BPSR_EPIC", "StarSEA", "StarASIA", "StarSEA_STEAM", "StarASIA_STEAM", "Star"];

    // Lock target
    public bool LockTargetEnabled { get; set; } = true;

    // Virtual-key code of whatever the player locks on with. The helper
    // watches this one key so it can scan for the lock record at the moment a
    // lock is actually held -- the only moment the scan can succeed. The
    // game's default is the middle mouse button (0x04); set 0 to switch the
    // trigger off, or another code if the binding differs. A controller has
    // no virtual-key code, which is why the helper has two other ways to
    // decide to scan and does not depend on this one.
    public int LockKeyVk { get; set; } = 0x04;

    // Lock target overlay
    public bool TargetOverlayEnabled { get; set; }
    public bool TargetOverlayTopMost { get; set; } = true;
    public double TargetOverlayLeft { get; set; } = double.NaN;
    public double TargetOverlayTop { get; set; } = double.NaN;
    public double TargetOverlayWidth { get; set; } = 240;
    public double TargetOverlayHeight { get; set; } = 44;
    public double TargetOverlayOpacity { get; set; } = 0.9;
    public double TargetOverlayFontSize { get; set; } = 14;
    public double TargetOverlayBorderThickness { get; set; } = 1;
    public uint TargetOverlayBorderArgb { get; set; } = 0xFF3FA7AD;
    public uint TargetOverlayBackgroundArgb { get; set; } = 0xF00A1418;
    public uint TargetOverlayTextArgb { get; set; } = 0xFFCFE8EA;

    // Map
    public RadarViewMode ViewMode { get; set; } = RadarViewMode.MiniMap;
    public RadarLabelMode LabelMode { get; set; } = RadarLabelMode.PartyAndBoss;
    public float BaseDisplayRangeMeters { get; set; } = 100f;
    public float OverviewMaxRangeMeters { get; set; } = 500f;
    public float Zoom { get; set; } = 1f;
    public float EnemyMaxDistanceMeters { get; set; } = 50f;
    public float BackgroundOpacity { get; set; } = 0.65f;

    // Live-tested on BPSR: 1 world unit == 1.000 m (see docs/tactical-map-design.md)
    public float WorldUnitsPerMeter { get; set; } = 1.0f;
    public bool DistanceCalibrationComplete { get; set; } = true;

    public bool ShowSelf { get; set; } = true;
    public bool ShowParty { get; set; } = true;
    public bool ShowPlayers { get; set; } = true;
    public bool ShowNormalMonsters { get; set; } = true;
    public bool ShowEliteMonsters { get; set; } = true;
    public bool ShowBosses { get; set; } = true;
    public bool ShowUnknownMonsters { get; set; }
    public bool ShowGimmicks { get; set; } = true;
    public bool ShowSelfDirection { get; set; } = true;

    public bool ShowDistanceRings { get; set; } = true;
    public bool ShowDistanceRing10m { get; set; } = true;
    public bool ShowDistanceRing20m { get; set; } = true;
    public bool ShowDistanceRing30m { get; set; } = true;
    public bool ShowDistanceRingLabels { get; set; } = true;

    public string EnemyNameIncludes { get; set; } = "";
    public string EnemyNameExcludes { get; set; } = "";
    public string PartyNameIncludes { get; set; } = "";
    public string PartyNameExcludes { get; set; } = "";

    [JsonIgnore]
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<RadarSettings>(File.ReadAllText(SettingsPath), JsonOptions);
                if (loaded != null)
                {
                    Instance = loaded;
                }
            }
        }
        catch
        {
            Instance = new RadarSettings();
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Instance, JsonOptions));
        }
        catch
        {
        }
    }
}
