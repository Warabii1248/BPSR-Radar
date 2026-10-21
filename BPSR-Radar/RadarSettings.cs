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

// Saved as its number: append new modes, never reorder.
public enum EnemyListSortMode
{
    Distance,
    // Lowest HP ratio first; HP not received last.
    HpRatio,
}

// Saved as its number: append new modes, never reorder.
public enum TargetOverlayHpMode
{
    Off,
    Bar,
    Percent,
    // Current / max HP.
    Actual,
}

public sealed class RadarSettings
{
    // Settings and the name cache. Check tools set BPSR_RADAR_DATA_DIR to a
    // scratch folder before anything loads: a check once closed a real
    // window, and its OnClosed save replaced the player's settings with test
    // values (2026-10-20).
    public static readonly string DataDir =
        Environment.GetEnvironmentVariable("BPSR_RADAR_DATA_DIR") is { Length: > 0 } dir ? dir :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BPSR-Radar");
    private static readonly string SettingsDir = DataDir;
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

    // Where the player dragged the floating settings window to; NaN until
    // they do. Opening, the on-screen clamp and the bottom pull-back never write it.
    public double SettingsWindowLeft { get; set; } = double.NaN;
    public double SettingsWindowTop { get; set; } = double.NaN;

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
    public bool TargetOverlayShowHp { get; set; } = true;
    // false: percentage, true: current / max HP.
    public bool TargetOverlayShowActualHp { get; set; }
    // null in files saved before the mode existed: derived from the two keys
    // above, which are still written so an older build reads the same choice.
    public TargetOverlayHpMode? TargetOverlayHp { get; set; }

    [JsonIgnore]
    public TargetOverlayHpMode EffectiveTargetOverlayHp =>
        TargetOverlayHp is { } mode && Enum.IsDefined(mode) ? mode
        : !TargetOverlayShowHp ? TargetOverlayHpMode.Off
        : TargetOverlayShowActualHp ? TargetOverlayHpMode.Actual
        : TargetOverlayHpMode.Percent;

    public void SetTargetOverlayHp(TargetOverlayHpMode mode)
    {
        TargetOverlayHp = mode;
        TargetOverlayShowHp = mode != TargetOverlayHpMode.Off;
        TargetOverlayShowActualHp = mode == TargetOverlayHpMode.Actual;
    }

    // Nearby enemy list overlay
    public bool EnemyListEnabled { get; set; } = true;
    public float EnemyListRadiusMeters { get; set; } = 10f;
    public bool EnemyListTopMost { get; set; } = true;
    public bool EnemyListHasPosition { get; set; }
    public double EnemyListLeft { get; set; }
    public double EnemyListTop { get; set; }
    public double EnemyListWidth { get; set; } = 300;
    // Last automatic height; retained in saved settings but not restored.
    public double EnemyListHeight { get; set; } = 320;
    public double EnemyListOpacity { get; set; } = 0.9;
    public double EnemyListFontSize { get; set; } = 14;
    public EnemyListSortMode EnemyListSort { get; set; } = EnemyListSortMode.Distance;
    // Rows past the limit are shown only as a "+N" count.
    public bool EnemyListLimitRows { get; set; }
    public int EnemyListMaxRows { get; set; } = 10;

    [JsonIgnore]
    public float EffectiveEnemyListRadiusMeters => float.IsFinite(EnemyListRadiusMeters)
        ? Math.Clamp(EnemyListRadiusMeters, 1f, 200f)
        : 10f;

    [JsonIgnore]
    public int EffectiveEnemyListMaxRows => EnemyListLimitRows
        ? Math.Clamp(EnemyListMaxRows, 1, 50)
        : int.MaxValue;

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
    // Monsters and gimmicks without current and maximum HP, on the map and in
    // the nearby list. Self, party and other players are never hidden by it.
    public bool HideEntitiesWithoutHp { get; set; }
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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Unpositioned target windows use NaN. Without this, saving ANY
        // setting before that window is opened silently fails.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

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
            // Defaults are used, and the next Save would overwrite the
            // unreadable file. Keep it so the player's settings can be recovered.
            try
            {
                File.Copy(SettingsPath, SettingsPath + $".unreadable-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: false);
            }
            catch
            {
            }
            Instance = new RadarSettings();
        }
    }

    // Written to a temporary file, flushed to disk, then swapped in, so a
    // crash mid-write leaves the previous settings rather than a truncated
    // file. (If power loss still leaves a bad file, Load keeps a copy.)
    // Set for --check: every save path (window close, timers, exit) becomes a no-op.
    public static bool ReadOnly;

    public static void Save()
    {
        if (ReadOnly) return;
        try
        {
            Directory.CreateDirectory(SettingsDir);
            string temp = SettingsPath + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] json = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Instance, JsonOptions));
                stream.Write(json, 0, json.Length);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, SettingsPath, overwrite: true);
        }
        catch
        {
        }
    }
}
