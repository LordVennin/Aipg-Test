using ARPG.Inventory;
using ARPG.Items;
using ARPG.Sim;
using ARPG.Skills;

namespace ARPG.Server;

/// <summary>
/// Join sanitization (ServerWorld partial). The join packet carries the CLIENT's
/// character JSON — an old save, a save from a build with different content, or a
/// hand-edited file. Anything the server would later dereference blindly (an item whose
/// base no longer exists, a modifier or skill id that was renamed, a null collection)
/// is repaired or dropped HERE, before the character touches the simulation.
/// Every repair is logged. Extend this whenever CharacterData gains a field.
/// </summary>
public partial class ServerWorld
{
    public const int MaxCharacterLevel = 100;
    public const int MaxGold = 1_000_000_000;

    /// <summary>Repair <paramref name="c"/> in place. Returns what was changed (also logged).</summary>
    public List<string> SanitizeCharacter(CharacterData c, string playerName)
    {
        var notes = new List<string>();
        void Note(string what) => notes.Add(what);

        // ---- collections: never null
        c.Story ??= new StoryProgress();
        c.Story.ScenesSeen ??= new List<string>();
        c.Inventory ??= new InventoryGrid { Width = 10, Height = 6 };
        c.Inventory.Items ??= new List<PlacedItem>();
        if (c.Inventory.Width is < 1 or > 40 || c.Inventory.Height is < 1 or > 40)
        {
            Note($"inventory size {c.Inventory.Width}x{c.Inventory.Height} reset to 10x6");
            c.Inventory.Width = 10;
            c.Inventory.Height = 6;
        }
        c.Equipment ??= new Dictionary<EquipSlot, ItemInstance>();
        c.Skills ??= new List<LearnedSkill>();
        if (c.Hotbar == null || c.Hotbar.Length != 5)
        {
            var bar = new string[5];
            if (c.Hotbar != null) Array.Copy(c.Hotbar, bar, Math.Min(5, c.Hotbar.Length));
            c.Hotbar = bar;
        }
        c.ShopSoldSlots ??= new List<int>();
        c.AllocatedPassives ??= new List<string>();
        c.Mercs ??= new List<MercData>();
        c.Stashes ??= new Dictionary<string, InventoryGrid>();
        c.UnlockedSkills ??= new HashSet<string>();
        c.BookGuarantees ??= new List<string>();
        c.Quests ??= new QuestLog();
        c.Quests.Active ??= new List<QuestProgress>();
        c.Quests.Completed ??= new List<string>();
        c.Mercs.RemoveAll(m => m == null);
        foreach (var key in c.Stashes.Keys.ToList())
        {
            var grid = c.Stashes[key];
            if (grid == null) { c.Stashes.Remove(key); continue; }
            grid.Items ??= new List<PlacedItem>();
            if (grid.Width is < 1 or > 40 || grid.Height is < 1 or > 40)
            {
                grid.Width = CharacterData.StashWidth;
                grid.Height = CharacterData.StashHeight;
            }
        }

        // ---- items: unknown bases are dropped, unknown modifiers stripped, values clamped
        var seenIds = new HashSet<Guid>();
        bool ItemOk(ItemInstance it, string where)
        {
            if (it == null) return false;
            if (string.IsNullOrEmpty(it.BaseItemId) || !Data.Items.TryGetValue(it.BaseItemId, out var itemBase))
            {
                Note($"dropped item with unknown base '{it.BaseItemId}' ({where})");
                return false;
            }
            it.Modifiers ??= new List<ItemModifierRoll>();
            int removed = it.Modifiers.RemoveAll(r =>
            {
                bool bad = r == null || string.IsNullOrEmpty(r.ModifierId) || !Data.Modifiers.ContainsKey(r.ModifierId);
                if (bad) Note($"removed unknown modifier '{r?.ModifierId}' from {it.BaseItemId} ({where})");
                return bad;
            });
            foreach (var r in it.Modifiers)
                if (float.IsNaN(r.Value) || float.IsInfinity(r.Value)) r.Value = 0f;
            if (it.InstanceId == Guid.Empty || !seenIds.Add(it.InstanceId))
            {
                it.InstanceId = Guid.NewGuid(); // duplicated / missing instance ids break moves
                seenIds.Add(it.InstanceId);
            }
            int maxStack = Math.Max(1, itemBase.MaxStack);
            if (it.StackCount > maxStack)
            {
                Note($"stack of {it.StackCount} {it.BaseItemId} clamped to {maxStack} ({where})");
                it.StackCount = maxStack;
            }
            if (it.StackCount < 1) it.StackCount = 1;
            if (it.ItemLevel < 1) it.ItemLevel = 1;
            if (it.ItemLevel > 200) it.ItemLevel = 200;
            it.FlaskCharges = Math.Clamp(it.FlaskCharges, 0, Math.Max(0, itemBase.FlaskChargesMax));
            it.MaxPrefixes = Math.Clamp(it.MaxPrefixes, 0, 8);
            it.MaxSuffixes = Math.Clamp(it.MaxSuffixes, 0, 8);
            return true;
        }
        void CleanGrid(InventoryGrid grid, string where) =>
            grid.Items.RemoveAll(pl => pl == null || !ItemOk(pl.Item, where));

        CleanGrid(c.Inventory, "bag");
        foreach (var (key, grid) in c.Stashes) CleanGrid(grid, $"stash {key}");
        foreach (var slot in c.Equipment.Keys.ToList())
        {
            var it = c.Equipment[slot];
            if (it == null || !Enum.IsDefined(typeof(EquipSlot), slot) || !ItemOk(it, $"equipped {slot}"))
                c.Equipment.Remove(slot);
            else if (!ItemBase.CompatibleSlots(it.GetBase(Data).Category).Contains(slot))
            {
                Note($"{it.BaseItemId} cannot be worn in {slot} — unequipped (dropped if the bag is full)");
                c.Equipment.Remove(slot);
                c.Inventory.TryAdd(Data, it);
            }
        }

        // ---- skills, hotbar, passives
        var knownSkills = new HashSet<string>();
        c.Skills.RemoveAll(sk =>
        {
            bool bad = sk == null || string.IsNullOrEmpty(sk.SkillId) || !Data.Skills.ContainsKey(sk.SkillId) ||
                       !knownSkills.Add(sk.SkillId);
            if (bad) Note($"removed unknown or duplicate skill '{sk?.SkillId}'");
            return bad;
        });
        foreach (var sk in c.Skills)
        {
            sk.Level = Math.Clamp(sk.Level, 1, SkillMath.MaxSkillLevel);
            if (float.IsNaN(sk.Experience) || float.IsInfinity(sk.Experience) || sk.Experience < 0) sk.Experience = 0;
            sk.Scrolls ??= new List<ItemInstance>();
            for (int i = 0; i < sk.Scrolls.Count; i++)
                if (sk.Scrolls[i] != null &&
                    (!ItemOk(sk.Scrolls[i], $"scroll slot of {sk.SkillId}") ||
                     sk.Scrolls[i].GetBase(Data).Category != ItemCategory.SkillScroll))
                    sk.Scrolls[i] = null;
        }
        for (int i = 0; i < c.Hotbar.Length; i++)
            if (c.Hotbar[i] != null && !knownSkills.Contains(c.Hotbar[i]))
            {
                Note($"cleared hotbar slot {i} (unknown or unlearned skill '{c.Hotbar[i]}')");
                c.Hotbar[i] = null;
            }
        var seenNodes = new HashSet<string>();
        c.AllocatedPassives.RemoveAll(id =>
        {
            bool bad = id == null || !Data.PassiveTree.ById.ContainsKey(id) || !seenNodes.Add(id);
            if (bad) Note($"removed unknown or duplicate passive '{id}'");
            return bad;
        });
        if (c.UnlockedSkills.RemoveWhere(id => id == null || !Data.Skills.ContainsKey(id)) > 0)
            Note("removed unknown skill unlocks");
        SanitizeQuestLog(c, Note);

        // ---- nonsense values
        if (c.Gold < 0) { Note($"gold {c.Gold} clamped to 0"); c.Gold = 0; }
        if (c.Gold > MaxGold) { Note($"gold {c.Gold} clamped to {MaxGold}"); c.Gold = MaxGold; }
        if (c.Level < 1) { Note($"level {c.Level} clamped to 1"); c.Level = 1; }
        if (c.Level > MaxCharacterLevel) { Note($"level {c.Level} clamped to {MaxCharacterLevel}"); c.Level = MaxCharacterLevel; }
        if (float.IsNaN(c.Experience) || float.IsInfinity(c.Experience) || c.Experience < 0) c.Experience = 0;
        c.BaseStrength = Math.Clamp(c.BaseStrength, 0, 999);
        c.BaseDexterity = Math.Clamp(c.BaseDexterity, 0, 999);
        c.BaseIntelligence = Math.Clamp(c.BaseIntelligence, 0, 999);
        if (c.AllocatedPassives.Count > PassiveTree.PointsForLevel(c.Level))
        {
            Note("more passives allocated than the level allows — passives reset");
            c.AllocatedPassives.Clear();
        }
        if (string.IsNullOrEmpty(c.ClassId) || Data.Classes.All(cl => cl.Id != c.ClassId))
            c.ClassId = Data.Classes.FirstOrDefault()?.Id ?? "warrior";

        foreach (var note in notes)
            Console.WriteLine($"[Server] Sanitized {playerName}'s character: {note}");
        return notes;
    }
}

public partial class ServerWorld
{
    /// <summary>Quest log repair: unknown quests go, steps and counts are clamped.</summary>
    private void SanitizeQuestLog(CharacterData c, Action<string> note)
    {
        var seen = new HashSet<string>();
        c.Quests.Completed.RemoveAll(id => id == null || Data.GetQuest(id) == null || !seen.Add(id));
        var active = new HashSet<string>();
        c.Quests.Active.RemoveAll(q =>
        {
            var def = q == null ? null : Data.GetQuest(q.QuestId ?? "");
            bool bad = def == null || def.Steps.Count == 0 || seen.Contains(q.QuestId) || !active.Add(q.QuestId);
            if (bad) note($"removed unknown or duplicate quest '{q?.QuestId}'");
            return bad;
        });
        foreach (var q in c.Quests.Active)
        {
            var def = Data.GetQuest(q.QuestId);
            q.Step = Math.Clamp(q.Step, 0, Math.Max(0, def.Steps.Count - 1));
            q.Count = Math.Clamp(q.Count, 0, Math.Max(1, def.Steps[q.Step].Count));
        }
    }
}
