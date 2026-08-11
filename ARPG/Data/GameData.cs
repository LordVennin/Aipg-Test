using ARPG.Items;
using ARPG.Skills;
using ARPG.Util;

namespace ARPG.Data;

/// <summary>An enemy type, loaded from Data/Enemies/*.json.</summary>
public class EnemyDefinition
{
    public string Id { get; set; }
    public string Name { get; set; }
    public int Level { get; set; } = 1;
    public float MaxHealth { get; set; } = 30;
    public float MoveSpeed { get; set; } = 2.5f;
    public float Damage { get; set; } = 5;
    public float AttackRange { get; set; } = 1.2f;
    public float AttackCooldown { get; set; } = 1.5f;
    public float AggroRange { get; set; } = 8f;
    public bool Ranged { get; set; }
    public float ProjectileSpeed { get; set; } = 8f;
    public float Radius { get; set; } = 0.4f;    // collision/visual size in tiles
    public float XpReward { get; set; } = 20;
    public string LootTableId { get; set; } = "default";
    public string Color { get; set; } = "C04040"; // art tint (RRGGBB hex)
    /// <summary>Procedural sprite style: "Zombie", "Ghoul", or empty for a plain token.</summary>
    public string SpriteStyle { get; set; } = "";
}

/// <summary>Base dodge tuning, loaded from Data/Config/dodge.json. Final values are these
/// bases scaled by the character's Dodge* stats (equipment/modifiers can change them).</summary>
public class DodgeConfig
{
    public float Distance { get; set; } = 3.0f;
    public float Duration { get; set; } = 0.25f;
    public float Cooldown { get; set; } = 2.0f;
    public float InvulnerabilityDuration { get; set; } = 0.3f;
}

/// <summary>Loot table, loaded from Data/LootTables/*.json.</summary>
public class LootTable
{
    public string Id { get; set; }
    /// <summary>Chance (0..1) that an equipment item drops at all.</summary>
    public float DropChance { get; set; } = 0.5f;
    /// <summary>Independent chance (0..1) that a Skill Scroll drops.</summary>
    public float ScrollDropChance { get; set; } = 0.08f;
    public int RarityWeightNormal { get; set; } = 50;
    public int RarityWeightMagic { get; set; } = 35;
    public int RarityWeightRare { get; set; } = 15;
    /// <summary>Relative weights per item category; missing categories use their DropWeight only.</summary>
    public Dictionary<ItemCategory, int> CategoryWeights { get; set; } = new();
}

/// <summary>
/// All loaded game content. Content lives in JSON files under Data/ and is loaded once at
/// startup; adding items/affixes/skills/scrolls requires no code changes.
/// </summary>
public class GameData
{
    public Dictionary<string, ItemBase> Items { get; } = new();
    public Dictionary<string, ItemModifier> Modifiers { get; } = new();
    public Dictionary<string, SkillDefinition> Skills { get; } = new();
    public Dictionary<string, ScrollDefinition> Scrolls { get; } = new();
    public Dictionary<string, EnemyDefinition> Enemies { get; } = new();
    public Dictionary<string, LootTable> LootTables { get; } = new();

    /// <summary>Skill level reached -> total scroll slots unlocked (Data/Config/scroll_slots.json).</summary>
    public Dictionary<int, int> ScrollSlotProgression { get; private set; } = new();

    public DodgeConfig Dodge { get; private set; } = new();

    public static GameData LoadFromDirectory(string dataDir)
    {
        var data = new GameData();

        foreach (var item in LoadAll<ItemBase>(Path.Combine(dataDir, "Items")))
            data.Items[item.Id] = item;
        foreach (var mod in LoadAll<ItemModifier>(Path.Combine(dataDir, "Modifiers")))
            data.Modifiers[mod.Id] = mod;
        foreach (var skill in LoadAll<SkillDefinition>(Path.Combine(dataDir, "Skills")))
            data.Skills[skill.Id] = skill;
        foreach (var scroll in LoadAll<ScrollDefinition>(Path.Combine(dataDir, "SkillScrolls")))
            data.Scrolls[scroll.Id] = scroll;
        foreach (var enemy in LoadAll<EnemyDefinition>(Path.Combine(dataDir, "Enemies")))
            data.Enemies[enemy.Id] = enemy;
        foreach (var table in LoadAll<LootTable>(Path.Combine(dataDir, "LootTables")))
            data.LootTables[table.Id] = table;

        string slotsPath = Path.Combine(dataDir, "Config", "scroll_slots.json");
        if (File.Exists(slotsPath))
        {
            var raw = Json.LoadFile<Dictionary<string, int>>(slotsPath);
            data.ScrollSlotProgression = raw.ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value);
        }

        string dodgePath = Path.Combine(dataDir, "Config", "dodge.json");
        if (File.Exists(dodgePath))
        {
            try { data.Dodge = Json.LoadFile<DodgeConfig>(dodgePath) ?? new DodgeConfig(); }
            catch (Exception e) { Console.WriteLine($"[Data] Failed to load dodge.json: {e.Message}"); }
        }

        Console.WriteLine($"[Data] Loaded {data.Items.Count} items, {data.Modifiers.Count} modifiers, " +
                          $"{data.Skills.Count} skills, {data.Scrolls.Count} scrolls, " +
                          $"{data.Enemies.Count} enemies, {data.LootTables.Count} loot tables.");
        return data;
    }

    public static GameData LoadDefault() =>
        LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Data"));

    /// <summary>Each file in the directory may contain either a single object or an array.</summary>
    private static List<T> LoadAll<T>(string dir)
    {
        var result = new List<T>();
        if (!Directory.Exists(dir)) return result;
        foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f))
        {
            string text = File.ReadAllText(file).TrimStart();
            try
            {
                if (text.StartsWith("["))
                    result.AddRange(Json.Load<List<T>>(text));
                else
                    result.Add(Json.Load<T>(text));
            }
            catch (Exception e)
            {
                Console.WriteLine($"[Data] Failed to load {file}: {e.Message}");
            }
        }
        return result;
    }

    public LootTable GetLootTable(string id) =>
        LootTables.GetValueOrDefault(id) ?? LootTables.GetValueOrDefault("default") ?? new LootTable { Id = "default" };
}
