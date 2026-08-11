using ARPG.Data;
using ARPG.Inventory;
using ARPG.Items;
using ARPG.Skills;

namespace ARPG.Sim;

/// <summary>A skill the character has learned, with its own level, XP and attached Skill Scrolls.</summary>
public class LearnedSkill
{
    public string SkillId { get; set; }
    public int Level { get; set; } = 1;
    public float Experience { get; set; }
    /// <summary>Attached Skill Scroll items, index = scroll slot. Entries may be null (empty slot).</summary>
    public List<ItemInstance> Scrolls { get; set; } = new();

    public SkillDefinition GetDefinition(GameData data) => data.Skills.GetValueOrDefault(SkillId);

    public IEnumerable<ScrollDefinition> ScrollDefinitions(GameData data)
    {
        foreach (var scrollItem in Scrolls)
        {
            if (scrollItem == null) continue;
            var itemBase = scrollItem.GetBase(data);
            if (itemBase.ScrollId != null && data.Scrolls.TryGetValue(itemBase.ScrollId, out var def))
                yield return def;
        }
    }
}

/// <summary>
/// The complete persistent state of one character. This same structure is:
///  - the local save file (Saves/&lt;name&gt;.json),
///  - the payload sent to the server on join,
///  - the authoritative state the server maintains and echoes back on every change.
/// </summary>
public class CharacterData
{
    public string Name { get; set; } = "Exile";
    public int Level { get; set; } = 1;
    public float Experience { get; set; }
    public int Gold { get; set; }
    public InventoryGrid Inventory { get; set; } = new() { Width = 10, Height = 6 };
    public Dictionary<EquipSlot, ItemInstance> Equipment { get; set; } = new();
    public List<LearnedSkill> Skills { get; set; } = new();
    /// <summary>Hotbar: slot 0 = primary attack (mouse), 1..4 = Skill1..Skill4 keys. Values are skill ids.</summary>
    public string[] Hotbar { get; set; } = new string[5];

    public float XpToNextLevel() => 40f + 25f * Level;

    public LearnedSkill GetSkill(string skillId) => Skills.FirstOrDefault(s => s.SkillId == skillId);

    public ItemInstance MainHand => Equipment.GetValueOrDefault(EquipSlot.MainHand);
    public ItemInstance OffHand => Equipment.GetValueOrDefault(EquipSlot.OffHand);

    /// <summary>Starting character: a club, a staff in the bag, two starter skills on the bar.</summary>
    public static CharacterData CreateNew(GameData data, string name)
    {
        var c = new CharacterData { Name = name };

        ItemInstance MakeNormal(string baseId) => new()
        {
            BaseItemId = baseId,
            ItemLevel = 1,
            Rarity = ItemRarity.Normal,
            BaseModifierLimit = data.Items.TryGetValue(baseId, out var b) ? b.BaseModifierLimit : 6,
        };

        if (data.Items.ContainsKey("wooden_club"))
            c.Equipment[EquipSlot.MainHand] = MakeNormal("wooden_club");
        if (data.Items.ContainsKey("oak_staff"))
            c.Inventory.TryAdd(data, MakeNormal("oak_staff"));

        if (data.Skills.ContainsKey("basic_strike"))
        {
            c.Skills.Add(new LearnedSkill { SkillId = "basic_strike" });
            c.Hotbar[0] = "basic_strike";
        }
        if (data.Skills.ContainsKey("mace_strike"))
        {
            c.Skills.Add(new LearnedSkill { SkillId = "mace_strike" });
            if (c.Hotbar[0] == null) c.Hotbar[0] = "mace_strike";
        }
        if (data.Skills.ContainsKey("fire_bolt"))
        {
            c.Skills.Add(new LearnedSkill { SkillId = "fire_bolt" });
            c.Hotbar[1] = "fire_bolt";
        }
        return c;
    }
}
