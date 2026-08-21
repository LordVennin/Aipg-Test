using ARPG.Data;
using ARPG.Inventory;
using ARPG.Items;
using ARPG.Skills;
using Microsoft.Xna.Framework;

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
    /// <summary>Starting class id (Data/Classes) — a starting KIT only, never a gate.</summary>
    public string ClassId { get; set; } = "warrior";
    /// <summary>Body silhouette: 0 = male, 1 = female (one human rig; races come later).</summary>
    public byte BodyStyle { get; set; }
    /// <summary>Preset index kept only as the fallback for saves made before free colors
    /// (SkinRgb null). New characters always store SkinRgb.</summary>
    public byte SkinTone { get; set; } = 2;
    /// <summary>Hair style (Appearance.Hair*); HairAuto = pre-hair save, derive from body.</summary>
    public byte HairStyle { get; set; } = Appearance.HairAuto;
    /// <summary>Exact 24-bit skin/hair colors (0xRRGGBB) — players mix ANY color, presets
    /// are just swatches. Null on old saves; the Effective* helpers supply the fallback.</summary>
    public int? SkinRgb { get; set; }
    public int? HairRgb { get; set; }

    public byte EffectiveHairStyle =>
        HairStyle == Appearance.HairAuto
            ? (BodyStyle == 1 ? Appearance.HairLong : Appearance.HairShort)
            : HairStyle;

    public Color EffectiveSkinColor => SkinRgb is int rgb
        ? Appearance.Unpack(rgb)
        : Appearance.SkinTones[Math.Clamp((int)SkinTone, 0, Appearance.SkinTones.Length - 1)];

    public Color EffectiveHairColor => HairRgb is int rgb
        ? Appearance.Unpack(rgb)
        : Appearance.HairColors[BodyStyle == 1 ? 2 : 1];
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

    /// <summary>Item storage keyed by CONTAINER id ("hub_stash" today; future player-room
    /// furniture adds more ids). Each container is its own grid, so storage is tied to
    /// the specific object in the world, never one global array.</summary>
    public Dictionary<string, InventoryGrid> Stashes { get; set; } = new();

    public const int StashWidth = 10, StashHeight = 8;

    public InventoryGrid GetStash(string containerId)
    {
        if (!Stashes.TryGetValue(containerId, out var grid))
            Stashes[containerId] = grid = new InventoryGrid { Width = StashWidth, Height = StashHeight };
        return grid;
    }

    public float XpToNextLevel() => 40f + 25f * Level;

    public LearnedSkill GetSkill(string skillId) => Skills.FirstOrDefault(s => s.SkillId == skillId);

    public ItemInstance MainHand => Equipment.GetValueOrDefault(EquipSlot.MainHand);
    public ItemInstance OffHand => Equipment.GetValueOrDefault(EquipSlot.OffHand);

    /// <summary>Starting character: the chosen class's KIT (weapon, off-hand, one skill),
    /// the starter flask pair, and 100 gold. Everything else is bought from the sanctum's
    /// skill trainer. Body/appearance is independent of class.</summary>
    public static CharacterData CreateNew(GameData data, string name, string classId = "warrior",
        byte bodyStyle = 0)
    {
        var cls = data.Classes.FirstOrDefault(cl => cl.Id == classId) ?? data.Classes.FirstOrDefault();
        var c = new CharacterData
        {
            Name = name,
            Gold = 100,
            ClassId = cls?.Id ?? "warrior",
            BodyStyle = bodyStyle,
        };

        ItemInstance MakeNormal(string baseId) => new()
        {
            BaseItemId = baseId,
            ItemLevel = 1,
            Rarity = ItemRarity.Normal,
            BaseModifierLimit = data.Items.TryGetValue(baseId, out var b) ? b.BaseModifierLimit : 6,
        };

        if (cls?.StartWeapon != null && data.Items.ContainsKey(cls.StartWeapon))
            c.Equipment[EquipSlot.MainHand] = MakeNormal(cls.StartWeapon);
        if (cls?.StartOffhand != null && data.Items.ContainsKey(cls.StartOffhand))
            c.Equipment[EquipSlot.OffHand] = MakeNormal(cls.StartOffhand);
        if (cls?.StartSkill != null && data.Skills.ContainsKey(cls.StartSkill))
        {
            c.Skills.Add(new LearnedSkill { SkillId = cls.StartSkill });
            c.Hotbar[0] = cls.StartSkill;
        }

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
        return c;
    }
}
