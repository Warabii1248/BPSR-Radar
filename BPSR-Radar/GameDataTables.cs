using System.IO;
using System.Text.Json;

namespace BpsrRadar;

public class NamedEntry
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class MonsterEntry : NamedEntry
{
    public int MonsterType { get; set; }
    public List<int> SkillIds { get; set; } = [];
    public List<List<float>> SightConfig { get; set; } = [];
    public List<float> HatredBuildType { get; set; } = [];
    public float MinAlertDis { get; set; }
    public float MaxAlertDis { get; set; }
    public float MonsterFightArea { get; set; }

    // Gimmick/map objects (route guides, turrets, interactables, traps) are
    // emitted as EntMonster but define no combat behavior in the table.
    // Null lists count as empty — missing data means no combat behavior.
    public bool IsGimmick =>
        (SkillIds?.Count ?? 0) == 0 &&
        (SightConfig?.All(row => row?.All(v => v == 0) ?? true) ?? true) &&
        (HatredBuildType?.All(v => v == 0) ?? true) &&
        MinAlertDis == 0 &&
        MaxAlertDis == 0 &&
        MonsterFightArea == 0;
}

public static class GameDataTables
{
    public static Dictionary<int, MonsterEntry> Monsters { get; private set; } = new();
    public static Dictionary<int, NamedEntry> Dummys { get; private set; } = new();
    public static Dictionary<int, NamedEntry> Scenes { get; private set; } = new();

    public static void Load()
    {
        Monsters = LoadTable<MonsterEntry>("MonsterTable.json");
        Dummys = LoadTable<NamedEntry>("DummyTable.json");
        Scenes = LoadTable<NamedEntry>("SceneTable.json");
    }

    private static Dictionary<int, T> LoadTable<T>(string fileName) where T : NamedEntry
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Data", fileName);
            if (!File.Exists(path))
            {
                return new();
            }

            var raw = JsonSerializer.Deserialize<Dictionary<string, T>>(File.ReadAllText(path));
            var result = new Dictionary<int, T>();
            if (raw == null)
            {
                return result;
            }

            foreach (var pair in raw)
            {
                if (int.TryParse(pair.Key, out int id) && pair.Value != null)
                {
                    result[id] = pair.Value;
                }
            }
            return result;
        }
        catch
        {
            return new();
        }
    }
}
