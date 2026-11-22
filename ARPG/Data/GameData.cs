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

    /// <summary>Legacy single damage value — used only when DamageTypes is empty
    /// (melee falls back to Blunt, ranged to Fire).</summary>
    public float Damage { get; set; } = 5;

    /// <summary>Typed attack damage: kind -> average amount per hit (rolled ±20%).
    /// An attack can mix types, e.g. a zombie's raking blow dealing Blunt + Acid.</summary>
    public Dictionary<DamageKind, float> DamageTypes { get; set; } = new();

    /// <summary>Percent resistance per damage type (negative values are weaknesses:
    /// -50 means taking 150% of that type). Missing entries count as 0.</summary>
    public Dictionary<DamageKind, float> Resistances { get; set; } = new();
    public float AttackRange { get; set; } = 1.2f;
    public float AttackCooldown { get; set; } = 1.5f;
    /// <summary>Melee telegraph: seconds of wind-up between committing to a swing and it
    /// landing (0 = legacy instant hit). The victim can leave the arc — the swing whiffs.</summary>
    public float AttackWindup { get; set; } = 0.4f;
    /// <summary>Pause after a swing resolves before the enemy moves or attacks again.</summary>
    public float AttackRecovery { get; set; } = 0.25f;
    /// <summary>Swing arc in degrees around the committed direction; the victim must
    /// still be inside it (and in reach) when the wind-up lands.</summary>
    public float AttackArc { get; set; } = 110f;
    /// <summary>Client animation and commitment style: "lunge" (claws; direction LOCKS at
    /// wind-up start — strafing beats it) or "sword" (a drawn blade; tracks its victim
    /// until just before impact — only a real dash escapes).</summary>
    public string AttackStyle { get; set; } = "lunge";
    /// <summary>Sword-style swings track their victim through the wind-up.</summary>
    public bool AttackTracks => AttackStyle == "sword";
    /// <summary>Number of procedural body variants this type draws (1 = every
    /// individual looks identical). Packs pick per-individual variants by id so a
    /// crowd of the same type doesn't read as one sprite stamped over and over.</summary>
    public int SpriteVariants { get; set; } = 1;
    public float AggroRange { get; set; } = 8f;
    public bool Ranged { get; set; }
    public float ProjectileSpeed { get; set; } = 8f;
    /// <summary>Marks a ranged enemy's projectile as a SPELL rather than an Attack —
    /// spell projectiles can never be Deflected. Data-driven per enemy type.</summary>
    public bool ProjectileIsSpell { get; set; }
    public float Radius { get; set; } = 0.4f;    // collision/visual size in tiles
    public float XpReward { get; set; } = 20;
    /// <summary>Ground-slam AoE attack (bosses): radius in tiles, 0 = no slam.</summary>
    public float SlamRadius { get; set; }
    public float SlamDamage { get; set; } = 14;
    public float SlamCooldown { get; set; } = 6f;
    /// <summary>Telegraph time: the red warning decal shows for this long before the
    /// slam resolves against resolve-time positions.</summary>
    public float SlamWindup { get; set; } = 0.9f;
    /// <summary>Telegraphed dash charge: the enemy stands still for DashWindup under a
    /// ground LINE telegraph, then barrels along it for heavy contact damage.
    /// DashDamage 0 = never dashes; DashMinLevel gates it to scaled-up spawns (the
    /// campaign's loop-2+ Gravelord), so the same def is dash-less early.</summary>
    public float DashDamage { get; set; }
    public float DashRange { get; set; } = 7f;
    public float DashSpeed { get; set; } = 13f;
    public float DashWindup { get; set; } = 1.0f;
    public float DashCooldown { get; set; } = 9f;
    public int DashMinLevel { get; set; } = 7;
    /// <summary>Reinforcement summon (bosses): enemy type conjured around this enemy
    /// while it fights. Empty = never summons.</summary>
    public string AddSpawnType { get; set; } = "";
    /// <summary>A second reinforcement type: summons alternate between the two.</summary>
    public string AddSpawnAltType { get; set; } = "";
    /// <summary>Summoned reinforcements spawn with this much health instead of their
    /// own (0 = their own): the Codex's leaves are real tomes with paper-thin life.</summary>
    public float AddSpawnHealth { get; set; }
    public int AddSpawnCount { get; set; } = 3;
    /// <summary>Seconds between summons, and the delay after FIRST engaging before the
    /// first one — a boss never opens the fight with its adds.</summary>
    public float AddSpawnCooldown { get; set; } = 24f;
    public float AddSpawnFirstDelay { get; set; } = 10f;
    /// <summary>Ranged telegraphed AoE spell (casters): a warning circle at the TARGET's
    /// position (locked at cast start — walk out to dodge it), resolving after
    /// CastWindup. CastRadius 0 = never casts.</summary>
    public float CastRadius { get; set; }
    public float CastDamage { get; set; } = 12f;
    public float CastRange { get; set; } = 7f;
    public float CastWindup { get; set; } = 1.1f;
    public float CastCooldown { get; set; } = 6f;
    public DamageKind CastKind { get; set; } = DamageKind.Dark;
    /// <summary>Comfort ring for casters (tiles): inside this distance to a threat the
    /// enemy BACKS AWAY instead of trading melee swipes — the swipe only comes out when
    /// it's cornered against a wall. 0 = never retreats (normal melee behavior).</summary>
    public float KeepDistance { get; set; }
    public string LootTableId { get; set; } = "default";
    public string Color { get; set; } = "C04040"; // art tint (RRGGBB hex)
    /// <summary>What sprays when this enemy is cut (RRGGBB): dark red for the fleshy,
    /// acid green for spitters. Physical hits burst it, corpses pool it, and melee
    /// weapons build it up coat by coat. EMPTY = bloodless (skeletons): no burst, no
    /// pool, nothing sticks to the weapon.</summary>
    public string Blood { get; set; } = "7E1A1A";
    /// <summary>Procedural sprite style: "Zombie", "Ghoul", "Skeleton", "Necro", or empty
    /// for a plain token.</summary>
    public string SpriteStyle { get; set; } = "";
    /// <summary>Undead: packs of these may spawn BURIED, lying unseen under the earth
    /// until a player comes near, then clawing up out of the ground.</summary>
    public bool Undead { get; set; }
    /// <summary>Hovers: drawn lifted off the ground with a bob and a detached shadow
    /// (the archive's tomes). Cosmetic — the simulation treats it like any walker.</summary>
    public bool Hover { get; set; }
    /// <summary>Self-lit (RRGGBB): the body casts this light on the gloom around it.
    /// Empty = unlit.</summary>
    public string Glow { get; set; } = "";
    /// <summary>Visual size multiplier on the sprite (0.75 = a runt, 1 = normal).</summary>
    public float SpriteScale { get; set; } = 1f;
    /// <summary>Art role within a SpriteStyle: Bandit "blade"/"bow"/"brute"/"captain",
    /// Goblin "skulker"/"slinger"/"shaman". Empty = the style's default look.</summary>
    public string SpriteRole { get; set; } = "";
    /// <summary>Ranged enemies: projectile sprite key ("Arrow", "Stone"). Empty = the
    /// default bolt (cold projectiles always draw the ice shard).</summary>
    public string ProjectileSprite { get; set; } = "";
    /// <summary>Spawned into a zone BELOW this level, the enemy keeps this level (its
    /// stats are authored for it): bandits and goblins never turn up as "level 1".
    /// 0 = follow the zone level exactly (all the undead).</summary>
    public int MinSpawnLevel { get; set; }
    /// <summary>Loose grouping for quests and audits ("undead", "goblin", "bandit", "tome").</summary>
    public string Family { get; set; } = "";
    /// <summary>Extra knockback (tiles) a landed melee swing shoves its victim (the
    /// Bandit Brute's heavy lunge). 0 = none. Stops at walls.</summary>
    public float AttackKnockback { get; set; }
    /// <summary>Chance (0..1) a landed melee swing leaves a player-side bleed; damage
    /// per second is BleedDamage for BleedDuration seconds.</summary>
    public float AttackBleedChance { get; set; }
    public float AttackBleedDps { get; set; }
    public float AttackBleedDuration { get; set; } = 4f;
    /// <summary>Specific drops on top of the loot table: each rolls independently
    /// (skill books from the monsters that carry them).</summary>
    public List<ExtraDrop> ExtraDrops { get; set; } = new();
}

public class ExtraDrop
{
    public string ItemId { get; set; }
    public float Chance { get; set; }
}

/// <summary>A weighted item id (loot table skill-book lists).</summary>
public class WeightedItem
{
    public string ItemId { get; set; }
    public int Weight { get; set; } = 1;
}

/// <summary>One pack composition a theme may roll. Entries are [enemy id, count]
/// pairs. MinLoop / MinLevel gate it: with neither set it is always available; if
/// either is set, EITHER gate opening makes it available (campaign loop reached OR
/// zone enemy level reached — today's "knights from loop 2 or level 4" rule).</summary>
public class PackPoolEntry
{
    public List<List<System.Text.Json.JsonElement>> Entries { get; set; } = new();
    public int Weight { get; set; } = 1;
    public int MinLoop { get; set; } = 1;
    public int MinLevel { get; set; } = 1;
    /// <summary>PromisedPacks only: station this pack if no rolled pack contains this enemy.</summary>
    public string RequireEnemy { get; set; }

    private (string typeId, int count)[] _members;
    /// <summary>The decoded roster.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public (string typeId, int count)[] Members => _members ??= ZoneTheme.ParseEntries(Entries).ToArray();

    /// <summary>Build one in code (the server's built-in undead lists).</summary>
    public static PackPoolEntry Of(int minLoop, int minLevel, params (string typeId, int count)[] members) =>
        new() { _members = members, MinLoop = minLoop, MinLevel = minLevel };

    public bool Available(int loop, int level) =>
        (MinLoop <= 1 && MinLevel <= 1) || (MinLoop > 1 && loop >= MinLoop) || (MinLevel > 1 && level >= MinLevel);
}

/// <summary>A theme's boss encounter: the boss plus its guards. The LAST listed pack
/// whose gate is open is used (list the early version first).</summary>
public class BossPackDef
{
    public string Boss { get; set; }
    public List<List<System.Text.Json.JsonElement>> Guards { get; set; } = new();
    public int MinLoop { get; set; } = 1;
    public int MinLevel { get; set; } = 1;

    private (string typeId, int count)[] _guards;
    [System.Text.Json.Serialization.JsonIgnore]
    public (string typeId, int count)[] GuardMembers => _guards ??= ZoneTheme.ParseEntries(Guards).ToArray();
    public static BossPackDef Of(string boss, int minLoop, int minLevel, params (string typeId, int count)[] guards) =>
        new() { Boss = boss, _guards = guards, MinLoop = minLoop, MinLevel = minLevel };
    public bool Available(int loop, int level) =>
        (MinLoop <= 1 && MinLevel <= 1) || (MinLoop > 1 && loop >= MinLoop) || (MinLevel > 1 && level >= MinLevel);
}

/// <summary>A weighted survival-wave enemy. MinWave keeps the nastier ones out of the opening waves.</summary>
public class WaveEnemy
{
    public string Id { get; set; }
    public int Weight { get; set; } = 1;
    public int MinWave { get; set; } = 1;
}

/// <summary>A quest (Data/Quests/quests.json): ordered steps and a reward. Quest logs
/// are per character; a step completes only for characters present when it happens.</summary>
public class QuestDefinition
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; } = "";
    /// <summary>Quest id that must be completed before this one is offered (empty = from the start).</summary>
    public string Requires { get; set; } = "";
    /// <summary>Side quests don't block the main line and show below it in the log.</summary>
    public bool Side { get; set; }
    public List<QuestStep> Steps { get; set; } = new();
    public int RewardGold { get; set; }
    public float RewardXp { get; set; }
    public string RewardItem { get; set; } = "";
}

/// <summary>One quest step. Type: reach_map (Target = a MapKind name or "zone:&lt;theme id&gt;"),
/// kill (Target = enemy id or "family:&lt;name&gt;", Count, optional MapKind), kill_boss
/// (Target = enemy id), talk (Target = npc id), turn_in (Target = item id or
/// "category:skill_book"), use_podium, complete_run.</summary>
public class QuestStep
{
    public string Type { get; set; }
    public string Target { get; set; } = "";
    public int Count { get; set; } = 1;
    public string MapKind { get; set; } = "";
    /// <summary>Tracker text, e.g. "Goblins slain" (shown as "Goblins slain 7/12").</summary>
    public string Text { get; set; } = "";
}

/// <summary>A scripted scene (Data/Cutscenes/*.json): timed lines, each focused on a
/// named spot of the current map. Anchors: "camp" (the wagon), "gate"/"exit" (the
/// exit door), "boss", "podium", "spawn", "npc:N" (the Nth NPC station), any with
/// "+dx,dy" appended (e.g. "camp+9,-1"). Edit the text here, not in code.</summary>
public class CutsceneDefinition
{
    public string Id { get; set; }
    public List<CutsceneStep> Steps { get; set; } = new();
}

public class CutsceneStep
{
    public string Anchor { get; set; } = "camp";
    public string Speaker { get; set; } = "";
    public string Line { get; set; } = "";
    public float Duration { get; set; } = 3.5f;
}

/// <summary>A starting class, loaded from Data/Classes/classes.json: purely a STARTING
/// KIT (weapon, off-hand, first skill) — classes never gate items or skills later, and
/// body/appearance is chosen independently.</summary>
public class ClassDefinition
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public string StartWeapon { get; set; }
    public string StartOffhand { get; set; }
    public string StartSkill { get; set; }
    /// <summary>Base attributes the class starts with (gear/passives add on top).
    /// These spreads make gear requirements BITE early: a fresh warrior can't wear
    /// silk, a fresh mage can't lift iron.</summary>
    public int StartStrength { get; set; } = 10;
    public int StartDexterity { get; set; } = 10;
    public int StartIntelligence { get; set; } = 10;
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

/// <summary>Crash: a mace skill's knockback driving an enemy into a wall, a structure
/// or another enemy. Loaded from Data/Config/mace_crash.json.</summary>
public class MaceCrashConfig
{
    /// <summary>Bonus damage to the thrown enemy, as a % of the hit that threw it.</summary>
    public float DamagePct { get; set; } = 40f;
    /// <summary>Share of the hit an enemy it was slammed INTO takes.</summary>
    public float SecondaryDamagePct { get; set; } = 20f;
    /// <summary>Burst of stun buildup (0-100 scale) on the thrown enemy / the one it hit.</summary>
    public float StunBuildup { get; set; } = 35f;
    public float SecondaryStunBuildup { get; set; } = 15f;
    public float StunDuration { get; set; } = 1.2f;
    /// <summary>Minimum knockback distance (tiles) for a Crash to count.</summary>
    public float MinKnockback { get; set; } = 0.5f;
}

/// <summary>
/// A zone's visual identity, loaded from Data/Zones/themes.json: terrain palette
/// (colors as RRGGBB hex), ground clutter style/density and the chance that a 1-high
/// wall tile renders as a themed feature (tree, crypt, rock spire...) on a base block.
/// Purely visual — simulation and collision are theme-independent.
/// </summary>
public class ZoneTheme
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Background { get; set; } = "101116";
    public string FloorA { get; set; } = "3A423A";
    public string FloorB { get; set; } = "343C36";
    /// <summary>Organic floor: per-tile shade picked from FloorA..FloorD by a seed hash,
    /// drawn gridless with tiny speckles — continuous terrain instead of a checkerboard.</summary>
    public bool OrganicFloor { get; set; }
    /// <summary>Ground materials the floor is made of ("grass:3A5632", "dirt", ...):
    /// baked pixel textures scattered in noise patches and blended at their edges.
    /// Listed staple-first; later entries draw over earlier ones at boundaries, and
    /// trails wear the "dirt" entry (or the last one). Empty = the old tinted tiles.</summary>
    public List<string> GroundMaterials { get; set; } = new();
    public string FloorC { get; set; }
    public string FloorD { get; set; }
    public string CliffFace { get; set; } = "808C80";
    public string ElevatedTop { get; set; } = "465044";
    public string WallFace { get; set; } = "8C85AD";
    public string WallTop { get; set; } = "545068";
    public string RampTint { get; set; } = "96A082";
    public string DeckA { get; set; } = "7A603E";
    public string DeckB { get; set; } = "70583A";
    public string DeckLip { get; set; } = "463624";
    /// <summary>Masonry rendering: floors draw as laid stone slabs with mortar seams and
    /// walls as coursed brick faces (the sanctum's purple stonework) instead of plain
    /// tinted tiles.</summary>
    public bool StoneBrick { get; set; }
    /// <summary>Zone-wide light level (RRGGBB) for the alpha-blend lighting pass: the
    /// scene multiplies by this everywhere no light source reaches. Null or near-white
    /// = full daylight, no lighting pass. Entity lights (players, bolts, the fountain)
    /// punch brightness back through the gloom.</summary>
    public string AmbientLight { get; set; }
    /// <summary>Dappled daylight: scatter seeded pools of warm sunlight over the map
    /// (light through the canopy / cloud gaps). Only meaningful with an AmbientLight
    /// dim enough for the bright patches to read.</summary>
    public bool SunPatches { get; set; }
    /// <summary>Optional weather on maps of this theme: "" (none, the default), "rain",
    /// "snow" or "wind". An attribute maps CAN have, never must — and future authored
    /// areas/interiors can override it per region.</summary>
    public string Weather { get; set; } = "";
    /// <summary>Clutter/feature sprite set: "graveyard", "tomb", "arid" or "forest".</summary>
    public string PropStyle { get; set; } = "graveyard";
    /// <summary>Chance per walkable tile of a small non-colliding clutter sprite.</summary>
    public float ClutterDensity { get; set; } = 0.05f;
    /// <summary>Chance per 1-level wall tile of a themed feature sprite on the block.</summary>
    public float WallFeatureChance { get; set; } = 0.3f;

    // ---- population (batch 85): who lives here. A theme WITHOUT a PackPool falls back
    // to the server's built-in undead lists.
    /// <summary>Weighted pack compositions for the map's pack spots.</summary>
    public List<PackPoolEntry> PackPool { get; set; } = new();
    /// <summary>Compositions for overlook spots (ranged on high ground). The LAST
    /// available entry is used. Empty = no overlook packs unless there is no PackPool.</summary>
    public List<PackPoolEntry> OverlookPool { get; set; } = new();
    /// <summary>Packs stationed if no rolled pack contains their RequireEnemy.</summary>
    public List<PackPoolEntry> PromisedPacks { get; set; } = new();
    /// <summary>The boss encounter for boss rooms on this theme.</summary>
    public List<BossPackDef> BossPacks { get; set; } = new();
    /// <summary>Weighted enemy ids for survival waves on this theme.</summary>
    public List<WaveEnemy> WaveEnemies { get; set; } = new();

    /// <summary>Decode [["id", count], ...] pairs. Malformed pairs are reported and skipped.</summary>
    public static List<(string typeId, int count)> ParseEntries(
        List<List<System.Text.Json.JsonElement>> raw, Action<string> onBad = null)
    {
        var result = new List<(string, int)>();
        foreach (var pair in raw ?? new())
        {
            if (pair is { Count: 2 } && pair[0].ValueKind == System.Text.Json.JsonValueKind.String &&
                pair[1].ValueKind == System.Text.Json.JsonValueKind.Number && pair[1].TryGetInt32(out int n))
                result.Add((pair[0].GetString(), n));
            else onBad?.Invoke("an entry is not [\"enemy_id\", count]");
        }
        return result;
    }
}

/// <summary>Loot table, loaded from Data/LootTables/*.json.</summary>
public class LootTable
{
    public string Id { get; set; }
    /// <summary>Chance (0..1) that an equipment item drops at all.</summary>
    public float DropChance { get; set; } = 0.5f;
    /// <summary>Independent chance (0..1) that a Skill Scroll drops.</summary>
    public float ScrollDropChance { get; set; } = 0.08f;
    /// <summary>Independent chance (0..1) that an Enchanting Scroll drops.</summary>
    public float EnchantScrollDropChance { get; set; } = 0.12f;
    /// <summary>Independent chance (0..1) that a UNIQUE drops (rare and boss kills add
    /// their own, much larger, chances on top).</summary>
    public float UniqueDropChance { get; set; } = 0.004f;
    /// <summary>Independent chance (0..1) that a Mercenary Contract drops (the defense
    /// mode's secondary currency — rare on purpose).</summary>
    public float MercContractDropChance { get; set; } = 0.012f;
    /// <summary>Independent chance (0..1) that the Flamethrower Blueprint drops.</summary>
    public float BlueprintDropChance { get; set; } = 0.003f;
    /// <summary>Independent chance (0..1) that a PET drops (rat or grimoire, coinflip) —
    /// the rarest ordinary find in the game.</summary>
    /// <summary>Chance a sealed warp scroll drops — the key to the portal loop.</summary>
    public float WarpScrollDropChance { get; set; } = 0.015f;
    public float PetDropChance { get; set; } = 0.0015f;
    /// <summary>Independent chance (0..1) that gold drops; amount scales with enemy level.</summary>
    public float GoldDropChance { get; set; } = 0.4f;
    public int GoldMin { get; set; } = 4;
    public int GoldMax { get; set; } = 14;
    public int RarityWeightNormal { get; set; } = 50;
    public int RarityWeightMagic { get; set; } = 35;
    public int RarityWeightRare { get; set; } = 15;
    /// <summary>Relative weights per item category; missing categories use their DropWeight only.</summary>
    public Dictionary<ItemCategory, int> CategoryWeights { get; set; } = new();
    /// <summary>Independent chance (0..1) that one skill book from SkillBooks drops.</summary>
    public float SkillBookDropChance { get; set; }
    public List<WeightedItem> SkillBooks { get; set; } = new();
}

/// <summary>An NPC type (Data/Npcs/*.json): name and dialogue lines. Merchants get their
/// shop behavior from code; the data file only carries flavor.</summary>
public class NpcDefinition
{
    public string Id { get; set; }
    public string Name { get; set; }
    public List<string> Dialogue { get; set; } = new();
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
    public Dictionary<string, NpcDefinition> Npcs { get; } = new();
    /// <summary>Starting classes in file order (Data/Classes/classes.json).</summary>
    public List<ClassDefinition> Classes { get; } = new();
    /// <summary>Zone visual themes in file order (Data/Zones/themes.json).</summary>
    public List<ZoneTheme> ZoneThemes { get; } = new();
    /// <summary>Scripted scenes by id (Data/Cutscenes/*.json).</summary>
    public Dictionary<string, CutsceneDefinition> Cutscenes { get; } = new();

    /// <summary>Quests in file order (Data/Quests/*.json).</summary>
    public List<QuestDefinition> Quests { get; } = new();
    public QuestDefinition GetQuest(string id) => Quests.FirstOrDefault(q => q.Id == id);

    internal void SetValidationErrors(List<string> errors) => ValidationErrors = errors;

    /// <summary>Skill level reached -> total scroll slots unlocked (Data/Config/scroll_slots.json).</summary>
    public Dictionary<int, int> ScrollSlotProgression { get; private set; } = new();

    public DodgeConfig Dodge { get; private set; } = new();
    /// <summary>The mace-wide Crash mechanic's numbers (Data/Config/mace_crash.json).</summary>
    public MaceCrashConfig MaceCrash { get; private set; } = new();

    /// <summary>The passive skill tree (Data/SkillTree/tree.json).</summary>
    public Skills.PassiveTree PassiveTree { get; private set; } = new();

    /// <summary>SHA-256 of the data files this was loaded from (see ContentFingerprint).</summary>
    public string Fingerprint { get; private set; } = "";
    /// <summary>Data files that failed to parse — content silently missing. The
    /// validator turns these into errors.</summary>
    public List<string> LoadFailures { get; } = new();
    /// <summary>Ids defined more than once ("Items: iron_mace") — the later definition won.</summary>
    public List<string> DuplicateIds { get; } = new();
    /// <summary>Validator output (DataValidator.Validate), shown on the main menu.</summary>
    public List<string> ValidationErrors { get; private set; } = new();

    public static GameData LoadFromDirectory(string dataDir)
    {
        var data = new GameData();
        data.Fingerprint = ContentFingerprint.Compute(dataDir);
        ContentFingerprint.Current = data.Fingerprint;

        void Register<T>(string kind, Dictionary<string, T> into, string folder, Func<T, string> idOf)
        {
            foreach (var def in data.LoadAll<T>(Path.Combine(dataDir, folder)))
            {
                string id = idOf(def);
                if (string.IsNullOrEmpty(id)) { data.LoadFailures.Add($"{folder}: an entry has no Id"); continue; }
                if (into.ContainsKey(id)) data.DuplicateIds.Add($"{kind}: {id}");
                into[id] = def;
            }
        }
        void RegisterList<T>(string kind, List<T> into, string folder, Func<T, string> idOf)
        {
            foreach (var def in data.LoadAll<T>(Path.Combine(dataDir, folder)))
            {
                if (into.Any(o => idOf(o) == idOf(def))) data.DuplicateIds.Add($"{kind}: {idOf(def)}");
                into.Add(def);
            }
        }
        Register("Items", data.Items, "Items", i => i.Id);
        Register("Modifiers", data.Modifiers, "Modifiers", m => m.Id);
        Register("Skills", data.Skills, "Skills", sk => sk.Id);
        Register("SkillScrolls", data.Scrolls, "SkillScrolls", sc => sc.Id);
        Register("Enemies", data.Enemies, "Enemies", e => e.Id);
        Register("LootTables", data.LootTables, "LootTables", t => t.Id);
        Register("Npcs", data.Npcs, "Npcs", n => n.Id);
        RegisterList("Zones", data.ZoneThemes, "Zones", t => t.Id);
        Register("Cutscenes", data.Cutscenes, "Cutscenes", c => c.Id);
        RegisterList("Classes", data.Classes, "Classes", c => c.Id);
        RegisterList("Quests", data.Quests, "Quests", q => q.Id);

        string slotsPath = Path.Combine(dataDir, "Config", "scroll_slots.json");
        if (File.Exists(slotsPath))
        {
            try
            {
                var raw = Json.LoadFile<Dictionary<string, int>>(slotsPath);
                data.ScrollSlotProgression = raw.ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value);
            }
            catch (Exception e) { data.RecordLoadFailure(slotsPath, e); }
        }

        string treePath = Path.Combine(dataDir, "SkillTree", "tree.json");
        if (File.Exists(treePath))
        {
            try { data.PassiveTree = Json.LoadFile<Skills.PassiveTree>(treePath) ?? new Skills.PassiveTree(); }
            catch (Exception e) { data.RecordLoadFailure(treePath, e); }
        }

        string dodgePath = Path.Combine(dataDir, "Config", "dodge.json");
        if (File.Exists(dodgePath))
        {
            try { data.Dodge = Json.LoadFile<DodgeConfig>(dodgePath) ?? new DodgeConfig(); }
            catch (Exception e) { data.RecordLoadFailure(dodgePath, e); }
        }

        string crashPath = Path.Combine(dataDir, "Config", "mace_crash.json");
        if (File.Exists(crashPath))
        {
            try { data.MaceCrash = Json.LoadFile<MaceCrashConfig>(crashPath) ?? new MaceCrashConfig(); }
            catch (Exception e) { data.RecordLoadFailure(crashPath, e); }
        }

        Console.WriteLine($"[Data] Loaded {data.Items.Count} items, {data.Modifiers.Count} modifiers, " +
                          $"{data.Skills.Count} skills, {data.Scrolls.Count} scrolls, " +
                          $"{data.Enemies.Count} enemies, {data.LootTables.Count} loot tables.");
        DataValidator.Validate(data);
        return data;
    }

    private void RecordLoadFailure(string file, Exception e)
    {
        string name = Path.GetFileName(Path.GetDirectoryName(file)) + "/" + Path.GetFileName(file);
        LoadFailures.Add($"{name}: {e.Message}");
        Console.WriteLine($"[Data] Failed to load {file}: {e.Message}");
    }

    public static GameData LoadDefault() =>
        LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Data"));

    /// <summary>Each file in the directory may contain either a single object or an array.</summary>
    private List<T> LoadAll<T>(string dir)
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
                // A file that fails to parse silently removes its content — record it
                // so the validator can make that loud (menu banner, failing nettest).
                RecordLoadFailure(file, e);
            }
        }
        return result;
    }

    public LootTable GetLootTable(string id) =>
        LootTables.GetValueOrDefault(id) ?? LootTables.GetValueOrDefault("default") ?? new LootTable { Id = "default" };
}
