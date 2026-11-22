using ARPG.Items;
using ARPG.Stats;

namespace ARPG.Data;

/// <summary>
/// Cross-reference check over the loaded content, run once after loading. A broken or
/// mistyped data file used to silently delete content (or crash the server tick much
/// later, when something dereferenced the missing id). Every problem found here is
/// logged, shown as a red banner on the main menu, and fails the nettest.
/// Extend it whenever a data file gains a new kind of reference.
/// </summary>
public static class DataValidator
{
    // Ids the CODE depends on (hard-coded in the server/world/UI). Renaming one of
    // these in the data files without touching the code must be loud.
    public static readonly string[] RequiredEnemies =
    {
        "grunt", "spitter", "bone_knight", "crypt_leaper", "grave_caller", "shambler",
        "gravelord", "barrowlord", "codex", "tome_frost", "tome_shade",
    };
    public static readonly string[] RequiredItems =
    {
        "minor_health_flask", "minor_mana_flask", "merc_contract", "flamethrower_blueprint",
    };
    public static readonly string[] RequiredThemes = { "graveyard", "tomb", "forest", "sanctum", "archive" };
    public static readonly string[] RequiredNpcs = { "merchant", "skill_trainer", "gambler", "researcher", "mercenary" };
    public static readonly string[] RequiredCutscenes = { "tut_intro", "tut_clearway", "tut_victory", "hub_arrival", "codex_scroll" };
    public static readonly string[] RequiredLootTables = { "default", "boss" };
    public static readonly string[] RequiredModifiers = { "warp_mode_defense", "warp_mode_survival" };
    public static readonly string[] RequiredModifierGroups = { "warp_zone", "warp_mode" };

    /// <summary>Validate and store the result on <paramref name="data"/>. Returns the errors.</summary>
    public static List<string> Validate(GameData data)
    {
        var errors = new List<string>();
        void Err(string msg) => errors.Add(msg);

        foreach (var failure in data.LoadFailures) Err($"failed to load {failure}");
        foreach (var dup in data.DuplicateIds) Err($"duplicate id — {dup}");

        void Require(string kind, IEnumerable<string> have, IEnumerable<string> need)
        {
            var set = new HashSet<string>(have.Where(h => h != null));
            foreach (var id in need)
                if (!set.Contains(id)) Err($"{kind} '{id}' is required by the code but missing from the data");
        }
        Require("enemy", data.Enemies.Keys, RequiredEnemies);
        Require("item", data.Items.Keys, RequiredItems);
        Require("zone theme", data.ZoneThemes.Select(t => t.Id), RequiredThemes);
        Require("npc", data.Npcs.Keys, RequiredNpcs);
        Require("cutscene", data.Cutscenes.Keys, RequiredCutscenes);
        Require("loot table", data.LootTables.Keys, RequiredLootTables);
        Require("modifier", data.Modifiers.Keys, RequiredModifiers);
        Require("modifier group", data.Modifiers.Values.Select(m => m.ModifierGroup), RequiredModifierGroups);

        // ---- enemies
        foreach (var e in data.Enemies.Values)
        {
            if (string.IsNullOrEmpty(e.Id)) { Err("an enemy has no Id"); continue; }
            if (!data.LootTables.ContainsKey(e.LootTableId ?? ""))
                Err($"enemy '{e.Id}': LootTableId '{e.LootTableId}' does not exist");
            foreach (var add in new[] { e.AddSpawnType, e.AddSpawnAltType })
                if (!string.IsNullOrEmpty(add) && !data.Enemies.ContainsKey(add))
                    Err($"enemy '{e.Id}': summons unknown enemy '{add}'");
            if (e.MaxHealth <= 0) Err($"enemy '{e.Id}': MaxHealth must be positive");
            if (e.AttackStyle is not ("lunge" or "sword")) Err($"enemy '{e.Id}': unknown AttackStyle '{e.AttackStyle}'");
            foreach (var drop in e.ExtraDrops ?? new())
            {
                if (!data.Items.ContainsKey(drop.ItemId ?? "")) Err($"enemy '{e.Id}': ExtraDrops item '{drop.ItemId}' does not exist");
                if (drop.Chance is < 0f or > 1f) Err($"enemy '{e.Id}': ExtraDrops chance {drop.Chance} is outside 0..1");
            }
        }

        // ---- classes
        if (data.Classes.Count == 0) Err("no classes loaded");
        foreach (var c in data.Classes)
        {
            if (string.IsNullOrEmpty(c.StartWeapon) || !data.Items.ContainsKey(c.StartWeapon))
                Err($"class '{c.Id}': StartWeapon '{c.StartWeapon}' does not exist");
            if (!string.IsNullOrEmpty(c.StartOffhand) && !data.Items.ContainsKey(c.StartOffhand))
                Err($"class '{c.Id}': StartOffhand '{c.StartOffhand}' does not exist");
            if (string.IsNullOrEmpty(c.StartSkill) || !data.Skills.ContainsKey(c.StartSkill))
                Err($"class '{c.Id}': StartSkill '{c.StartSkill}' does not exist");
        }

        // ---- modifiers (a mistyped StatAffected/category NAME fails the whole file to
        // parse and shows up as a load failure; numeric junk is caught here)
        foreach (var m in data.Modifiers.Values)
        {
            if (!Enum.IsDefined(typeof(StatType), m.StatAffected))
                Err($"modifier '{m.Id}': StatAffected {(int)m.StatAffected} is not a StatType");
            foreach (var cat in m.CompatibleItemCategories ?? new())
                if (!Enum.IsDefined(typeof(ItemCategory), cat))
                    Err($"modifier '{m.Id}': category {(int)cat} is not an ItemCategory");
            if (m.MaximumValue < m.MinimumValue) Err($"modifier '{m.Id}': MaximumValue < MinimumValue");
        }

        // ---- items
        foreach (var it in data.Items.Values)
        {
            if (!Enum.IsDefined(typeof(ItemCategory), it.Category))
                Err($"item '{it.Id}': category {(int)it.Category} is not an ItemCategory");
            if (!string.IsNullOrEmpty(it.ScrollId) && !data.Scrolls.ContainsKey(it.ScrollId))
                Err($"item '{it.Id}': ScrollId '{it.ScrollId}' does not exist");
            foreach (var stat in it.BaseStats.Keys)
                if (!Enum.IsDefined(typeof(StatType), stat))
                    Err($"item '{it.Id}': base stat {(int)stat} is not a StatType");
            if (it.InventoryWidth < 1 || it.InventoryHeight < 1) Err($"item '{it.Id}': inventory size must be at least 1x1");
            foreach (var skillId in it.UnlocksSkills ?? new())
                if (!data.Skills.ContainsKey(skillId ?? ""))
                    Err($"item '{it.Id}': UnlocksSkills names unknown skill '{skillId}'");
            if (it.UnlocksSkills is { Count: > 0 } && it.Category != ItemCategory.Curio)
                Err($"item '{it.Id}': skill books must be Curio items");
        }

        // ---- loot tables
        foreach (var t in data.LootTables.Values)
            foreach (var book in t.SkillBooks ?? new())
                if (!data.Items.TryGetValue(book.ItemId ?? "", out var bookBase) || bookBase.UnlocksSkills is not { Count: > 0 })
                    Err($"loot table '{t.Id}': SkillBooks entry '{book.ItemId}' is not a skill book");

        // ---- zone themes: pack pools
        foreach (var theme in data.ZoneThemes)
        {
            void CheckEntries(string where, List<List<System.Text.Json.JsonElement>> entries)
            {
                foreach (var (id, count) in ZoneTheme.ParseEntries(entries, bad => Err($"theme '{theme.Id}': {where}: {bad}")))
                {
                    if (!data.Enemies.ContainsKey(id)) Err($"theme '{theme.Id}': {where} names unknown enemy '{id}'");
                    if (count < 1) Err($"theme '{theme.Id}': {where} has a count below 1 for '{id}'");
                }
            }
            foreach (var pack in theme.PackPool ?? new())
            {
                if (pack.Entries == null || pack.Entries.Count == 0) Err($"theme '{theme.Id}': a PackPool entry is empty");
                CheckEntries("PackPool", pack.Entries);
                if (pack.Weight <= 0) Err($"theme '{theme.Id}': a PackPool entry has a non-positive Weight");
            }
            foreach (var pack in theme.OverlookPool ?? new()) CheckEntries("OverlookPool", pack.Entries);
            foreach (var pack in theme.PromisedPacks ?? new())
            {
                CheckEntries("PromisedPacks", pack.Entries);
                if (!data.Enemies.ContainsKey(pack.RequireEnemy ?? "")) Err($"theme '{theme.Id}': PromisedPacks RequireEnemy '{pack.RequireEnemy}' does not exist");
            }
            if (theme.PackPool is { Count: > 0 } && !theme.PackPool.Any(p => p.MinLoop <= 1 && p.MinLevel <= 1))
                Err($"theme '{theme.Id}': PackPool has no entry available at loop 1 / level 1");
            foreach (var boss in theme.BossPacks ?? new())
            {
                if (!data.Enemies.ContainsKey(boss.Boss ?? "")) Err($"theme '{theme.Id}': BossPack boss '{boss.Boss}' does not exist");
                CheckEntries("BossPack guards", boss.Guards);
            }
            foreach (var wave in theme.WaveEnemies ?? new())
            {
                if (!data.Enemies.ContainsKey(wave.Id ?? "")) Err($"theme '{theme.Id}': WaveEnemies names unknown enemy '{wave.Id}'");
                if (wave.Weight <= 0) Err($"theme '{theme.Id}': WaveEnemies '{wave.Id}' has a non-positive Weight");
            }
        }

        // ---- warp zone seals -> themes
        foreach (var m in data.Modifiers.Values.Where(m => m.ModifierGroup == "warp_zone"))
        {
            if (string.IsNullOrEmpty(m.ZoneThemeId) || data.ZoneThemes.All(t => t.Id != m.ZoneThemeId))
                Err($"modifier '{m.Id}': ZoneThemeId '{m.ZoneThemeId}' is not a zone theme");
            if (string.IsNullOrEmpty(m.ZoneName)) Err($"modifier '{m.Id}': warp zone seals need a ZoneName");
        }

        // ---- passive tree
        var nodeIds = new HashSet<string>(data.PassiveTree.Nodes.Select(n => n.Id));
        foreach (var pair in data.PassiveTree.Connections)
            foreach (var id in pair)
                if (!nodeIds.Contains(id)) Err($"passive tree: connection names unknown node '{id}'");

        // ---- quests
        foreach (var q in data.Quests)
        {
            if (string.IsNullOrEmpty(q.Id)) { Err("a quest has no Id"); continue; }
            if (q.Steps == null || q.Steps.Count == 0) Err($"quest '{q.Id}': no steps");
            if (!string.IsNullOrEmpty(q.Requires) && data.Quests.All(o => o.Id != q.Requires))
                Err($"quest '{q.Id}': Requires unknown quest '{q.Requires}'");
            if (!string.IsNullOrEmpty(q.RewardItem) && !data.Items.ContainsKey(q.RewardItem))
                Err($"quest '{q.Id}': RewardItem '{q.RewardItem}' does not exist");
            foreach (var step in q.Steps ?? new())
            {
                string where = $"quest '{q.Id}' step '{step.Text}'";
                switch (step.Type)
                {
                    case "kill":
                    case "kill_boss":
                        if (!data.Enemies.ContainsKey(step.Target ?? "") && !(step.Target ?? "").StartsWith("family:"))
                            Err($"{where}: unknown enemy '{step.Target}'");
                        if ((step.Target ?? "").StartsWith("family:") && data.Enemies.Values.All(e => e.Family != step.Target[7..]))
                            Err($"{where}: no enemy belongs to family '{step.Target[7..]}'");
                        break;
                    case "reach_map":
                        if (!Enum.TryParse<World.MapKind>(step.Target, out _) && data.ZoneThemes.All(t => "zone:" + t.Id != step.Target))
                            Err($"{where}: '{step.Target}' is neither a MapKind nor zone:<theme id>");
                        break;
                    case "talk":
                        if (!data.Npcs.ContainsKey(step.Target ?? "")) Err($"{where}: unknown npc '{step.Target}'");
                        break;
                    case "turn_in":
                        if (!data.Items.ContainsKey(step.Target ?? "") && step.Target != "category:skill_book")
                            Err($"{where}: unknown item '{step.Target}'");
                        break;
                    case "use_podium":
                    case "complete_run":
                        break;
                    default:
                        Err($"{where}: unknown step type '{step.Type}'");
                        break;
                }
                if (!string.IsNullOrEmpty(step.MapKind) && !Enum.TryParse<World.MapKind>(step.MapKind, out _))
                    Err($"{where}: MapKind '{step.MapKind}' is not a MapKind");
                if (step.Count < 1) Err($"{where}: Count must be at least 1");
            }
        }

        data.SetValidationErrors(errors);
        if (errors.Count == 0) Console.WriteLine("[Data] Validation passed.");
        else
        {
            Console.WriteLine($"[Data] VALIDATION FAILED — {errors.Count} problem(s):");
            foreach (var e in errors) Console.WriteLine($"[Data]   - {e}");
        }
        return errors;
    }
}
