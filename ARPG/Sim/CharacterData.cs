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

    /// <summary>Character level the current merchant stock was rolled for. When it no longer
    /// matches Level the shop rerolls (and sold slots reset). Persisted with the save, so
    /// leaving and rejoining never rerolls the shop within a level.</summary>
    public int ShopLevel { get; set; }
    /// <summary>Stock slot indices already purchased at ShopLevel (they stay sold out).</summary>
    public List<int> ShopSoldSlots { get; set; } = new();

    /// <summary>Allocated passive tree node ids (validated server-side on allocation).</summary>
    public List<string> AllocatedPassives { get; set; } = new();

    public float XpToNextLevel() => 40f + 25f * Level;

    public LearnedSkill GetSkill(string skillId) => Skills.FirstOrDefault(s => s.SkillId == skillId);

    public ItemInstance MainHand => Equipment.GetValueOrDefault(EquipSlot.MainHand);
    public ItemInstance OffHand => Equipment.GetValueOrDefault(EquipSlot.OffHand);

    /// <summary>Starting character: a club, a staff in the bag, 100 gold, and just the
    /// two free skills — Mace Strike and Fire Bolt. Everything else is bought from the
    /// sanctum's skill trainer.</summary>
    public static CharacterData CreateNew(GameData data, string name)
    {
        var c = new CharacterData { Name = name, Gold = 100 };

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

        // The starter flask pair, full. Charges never regenerate — the sanctum
        // fountain refills them between runs.
        if (data.Items.TryGetValue("minor_health_flask", out var hpFlaskBase))
        {
            var hpFlask = MakeNormal("minor_health_flask");
            hpFlask.FlaskCharges = hpFlaskBase.FlaskChargesMax;
            c.Equipment[EquipSlot.Flask1] = hpFlask;
        }
        if (data.Items.TryGetValue("minor_mana_flask", out var mpFlaskBase))
        {
            var mpFlask = MakeNormal("minor_mana_flask");
            mpFlask.FlaskCharges = mpFlaskBase.FlaskChargesMax;
            c.Equipment[EquipSlot.Flask2] = mpFlask;
        }

        if (data.Skills.ContainsKey("basic_strike"))
        {
            c.Skills.Add(new LearnedSkill { SkillId = "basic_strike" });
            c.Hotbar[0] = "basic_strike";
        }
        if (data.Skills.ContainsKey("fire_bolt"))
        {
            c.Skills.Add(new LearnedSkill { SkillId = "fire_bolt" });
            c.Hotbar[1] = "fire_bolt";
        }
        return c;
    }
}
