namespace BpsrRadar;

// Minimal EN/JA string helper. S.T(en, ja) picks by RadarSettings.Language.
internal static class S
{
    public static bool Ja =>
        string.Equals(RadarSettings.Instance.Language, "ja", StringComparison.OrdinalIgnoreCase);

    public static string T(string en, string ja) => Ja ? ja : en;
}
