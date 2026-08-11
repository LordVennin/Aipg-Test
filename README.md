# Scrollbound — Isometric Multiplayer ARPG Prototype

A playable isometric action-RPG prototype written **in plain C# with MonoGame** (no game
engine/editor): WASD movement, rebindable controls, real-time combat with dodge and
knockback, host-authoritative direct-IP multiplayer, grid inventory, equipment, randomized
prefix/suffix loot with **per-item rolled prefix/suffix slot caps**, gold drops with
modifier-based item values, **Enchanting Scrolls** (stackable PoE-orb-style crafting
currency — right-click a scroll, then click an item to apply it), learnable skills
with levels, Skill Scrolls, a character sheet with per-damage-type DPS,
procedural pixel-art sprites, data-driven JSON content and local JSON saves.

Crafting rules: white items have no modifiers, blue (magic) items hold at most 2, gold
(rare) items are limited only by the item's own rolled slots (3-8 total, 5 typical,
8 extremely rare). The **Scroll of Gilding** upgrades a blue (magic) item to gold
(rare) and adds one random modifier. The Scroll of Sealing adds +1 slot to each side
and permanently Seals the item against further crafting.

All artwork is generated at runtime (colored shapes) — this is intentionally a systems
prototype, not a visual showcase.

---

## 1. Prerequisites

| Requirement | Notes |
|---|---|
| .NET SDK 8.0 | `dotnet --version` should print 8.x. Install from https://dotnet.microsoft.com/download or `apt install dotnet-sdk-8.0` on Ubuntu 24.04. |
| Desktop OS with OpenGL | Windows, Linux or macOS. MonoGame DesktopGL ships its own native SDL/OpenGL bindings via NuGet — nothing else to install. |
| Internet on first build | NuGet restores `MonoGame.Framework.DesktopGL`, `LiteNetLib` and `FontStashSharp.MonoGame`. |

## 2. Building

```bash
cd ARPG
dotnet build
```

## 3. Running

```bash
cd ARPG
dotnet run                # normal launch (main menu)
dotnet run -- --sp        # skip the menu, go straight into single player
dotnet run -- --nettest   # headless automated multiplayer self-test (no window needed)
```

`--nettest` starts a real server plus **two** real clients over loopback UDP and asserts
36 synchronization checks (join, movement, combat, enemy death, host loot generation,
identical modifiers on both peers, exclusive pickup, equipping, scroll attachment,
disconnect resilience). Exit code 0 = all passed.

## 4. Hosting a game

1. Launch → **Host Game**.
2. Enter your player name and a port (**default 7777**).
3. **Start Hosting**. The server listens on **0.0.0.0** (all interfaces), so LAN peers and
   virtual-LAN peers (ZeroTier, NordVPN Meshnet, Tailscale, …) can connect with no extra
   configuration. The host plays as a normal client connected over loopback.

**Single Player** is exactly the same thing minus remote players: it starts a local server
on a loopback OS-assigned port and connects to it, so single player and multiplayer share
one authoritative simulation path.

## 5. Joining through direct IP

1. Launch → **Join Game**.
2. Enter your player name, the host's IP (`192.168.1.50`, a ZeroTier IP like
   `100.64.201.113`, or `127.0.0.1` for a second instance on the same machine) and the
   port (**7777** by default).
3. **Connect**. Errors (invalid IP, timeout, host unreachable, server full, version
   mismatch) are shown as readable messages, not crashes.

**Default multiplayer port: `7777` (UDP).** Open/forward it if hosting across a firewall.
No NAT traversal / matchmaking / accounts — direct IP only, by design.

## 6. Controls (all rebindable in Options)

| Action | Default |
|---|---|
| Move North / South / West / East | `W` / `S` / `A` / `D` (screen-relative, isometric-corrected, normalized diagonals) |
| Primary attack (hotbar slot 1) | Left mouse (aimed at cursor) |
| Skills 2–5 | `1` `2` `3` `4` |
| Dodge (dash with i-frames) | `Space` (direction = movement keys, else facing) |
| Inventory | `I` |
| Skill Menu | `K` |
| Character Sheet (defenses, resistances, skill DPS by damage type) | `C` |
| Interact / pick up nearest item | `F` (or click an item's ground label) |
| Debug menu | `F1` |
| Pause / close panels / menu | `Escape` |

Bindings are saved to `Saves/settings.json` next to the executable and restored on start.
The Options screen (main menu, or Pause → Options in game) also has persistent
**Damage Numbers** and **Enemy Health Bars** toggles.

**Dodge** base distance/duration/cooldown/i-frame duration are configured in
`Data/Config/dodge.json` and routed through the stat system (`DodgeDistance`,
`DodgeDuration`, `DodgeCooldownRecovery`, `DodgeInvulnerability` stats — equipment
modifiers can scale them). The cooldown and invulnerability are enforced
server-side; the dash movement is client-predicted for responsiveness. Damage is
shown via a networked damage-event system: the server broadcasts every damage
application and clients render floating combat numbers from those events.

## 7. Source directory map

```
ARPG/
├── Program.cs            entry point (+ --nettest / --sp flags)
├── GameMain.cs           window, game loop, screen switching, session startup
├── Core/                 InputManager (rebindable actions), GameSettings, net constants
├── Util/                 JSON config, System.Numerics<->XNA vector helpers
├── Stats/                StatType/StatCollection + StatCalculator (single place where
│                         base + equipment + modifiers + temporary effects combine)
├── Items/                ItemBase (definitions), ItemInstance (generated items),
│                         ItemModifier (prefix/suffix defs), LootGenerator
├── Inventory/            InventoryGrid (grid placement/collision/swap logic)
├── Skills/               SkillDefinition, Skill Scroll definitions/tags, SkillMath
│                         (levels, scroll slots, effective skill stats)
├── Sim/                  CharacterData — the one structure that is save file,
│                         join payload and authoritative server state
├── World/                GameMap: seeded generation + collision (world coordinates)
├── Server/               Authoritative simulation: ServerWorld (AI, combat, loot,
│                         pickups), ServerCharacterOps (inventory/equip/scroll moves),
│                         GameServer (LiteNetLib host + packet translation)
├── Net/                  Packets (protocol), GameClient, ClientWorld (interpolated view)
├── Render/               TextureGen (runtime placeholder art), FontManager, IsoCamera
│                         (world<->screen conversion), WorldRenderer
├── UI/                   Minimal widget kit + MainMenu/Host/Join/Options screens, HUD,
│                         InventoryUI, SkillMenuUI, DebugUI, ItemTooltip, PlayScreen
├── Persistence/          SaveManager (character JSON saves)
├── Testing/              HeadlessNetTest (automated 2-client multiplayer test)
├── Data/                 ALL game content as JSON (items, modifiers, skills, scrolls,
│                         enemies, loot tables, scroll-slot progression)
└── Content/Fonts/        optional bundled TTFs — if absent, FontManager falls back to
                          common OS-installed fonts (DejaVu/Liberation on Linux,
                          Segoe UI/Arial on Windows, Arial/Helvetica on macOS)
```

## 8. Authoritative networking model

- **Host-authoritative.** The host runs `GameServer` + `ServerWorld`: enemy spawning/AI,
  all damage, deaths, XP, **loot generation**, world drops, pickups and every character
  mutation (inventory moves, equipping, scroll attachment, learning skills, hotbar).
- **Clients send requests, not results**: "use skill X at position Y", "pick up item Z",
  "move item from A to B". The server validates (cooldowns, weapon category, range,
  inventory space, scroll tag compatibility, slot unlocks) and broadcasts outcomes.
  A failed request simply re-syncs the client's character state.
- **Movement** is client-predicted for responsiveness and sanity-clamped server-side
  (bounds + wall check). It is the one deliberately client-trusted input; everything
  persistent is server-decided.
- **Delivery**: player/enemy position snapshots go unreliable at 20/10 Hz (newest wins);
  everything that matters (joins, spawns, deaths, health, loot, character state) goes
  `ReliableOrdered`.
- **Single player = the same stack**: local server + loopback client, so there is exactly
  one gameplay code path.
- The map is generated deterministically from a seed the server sends on join, so only
  the seed crosses the wire.

### Loot flow (why items are identical on every peer)

```
enemy dies on server → LootGenerator rolls base/ilvl/rarity/affix count
  → clamps count against THAT ITEM's modifier limit → rolls affix values once
  → ItemInstance with unique InstanceId → serialized JSON broadcast to all clients
```
Clients never reroll anything — they deserialize the exact generated item. Pickups are
requests; the server checks existence + range + inventory space, so two players can
never both take the same drop.

## 9. Item serialization

`ItemInstance` is plain JSON everywhere (network packets, saves, world drops):

```json
{
  "InstanceId": "…guid…",
  "BaseItemId": "iron_mace",
  "ItemLevel": 7,
  "Rarity": "Rare",
  "BaseModifierLimit": 6,
  "Modifiers": [ { "ModifierId": "brutal", "Value": 14 }, … ]
}
```

Instances reference their `ItemBase` by id (definitions are never copied), and rolls are
stored, never regenerated. The character save (`Saves/char_<name>.json`) is the same
`CharacterData` JSON the server syncs during play — inventory placements, equipment,
learned skills with levels/XP/attached scrolls, and hotbar assignments included.

## 10. Extending the game (no engine code changes needed)

- **New item base**: add an object to any JSON file in `Data/Items/` (or a new file) with
  `Id`, `Name`, `Category`, `InventoryWidth/Height`, `RequiredLevel`, `BaseModifierLimit`,
  `BaseStats`, `DropWeight`. It immediately enters the loot pool.
- **New prefix**: add to `Data/Modifiers/prefixes.json` with `"AffixType": "Prefix"`,
  a `StatAffected` (see `Stats/StatType.cs`), value range, `Weight`, `ModifierGroup`
  (mutual exclusion) and `CompatibleItemCategories`. Modifiers come in 10 tiers per
  family (`Tier`, `MinimumItemLevel` gates the strong tiers to high-level items; all
  tiers of a family share one `ModifierGroup` so they can never stack).
- **New suffix**: same, in `suffixes.json` with `"AffixType": "Suffix"`.
- **Damage types**: physical damage is split into Thrust/Blunt/Slash (armor-mitigated;
  the weapon category picks the subtype — maces are Blunt) plus Fire/Cold/Lightning/
  Acid/Dark/Light (each with its own resistance) and unresisted Arcane. "Added X
  Damage" weapon modifiers attach typed components to attack hits.
- **New weapon category**: add a value to `ItemCategory`, map it to slots in
  `ItemBase.CompatibleSlots`, and add bases in JSON. Inventory, tooltips, loot and the
  skill system are category-driven — skills reference weapon *categories*
  (`"RequiredWeapon": "Mace"`), never item names.
- **New skill**: add to `Data/Skills/`, choosing an `Archetype`
  (`MeleeStrike` / `MeleeArea` / `Projectile` / `AreaBurst`), tags, damage/scaling
  fields. A genuinely new behavior means one new archetype case in
  `ServerWorld.UseSkill` — nothing else changes.
- **New Skill Scroll**: add to `Data/SkillScrolls/` (a `RequiredTag` + a list of
  `Effects` over `ScrollStat` values), plus a 1x1 `SkillScroll`-category item in
  `Data/Items/scrolls.json` whose `ScrollId` points at it. Tag-based compatibility means
  it automatically works with every current and future skill carrying that tag.
- **New Enchanting Scroll**: add an entry to `Data/Items/enchant_scrolls.json` with an
  `EnchantType` (see `Items/EnchantSystem.cs`), `MaxStack`, `DropWeight` and `SpriteColor`.
  A genuinely new crafting behavior is one new case in `EnchantSystem.Apply`.
- **Skill Scroll slot progression**: edit `Data/Config/scroll_slots.json`
  (`"skill level reached": total slots` — currently 2→1, 4→2, 6→3, 8→4).
- **Changing an item's Modifier Limit**: set `BaseModifierLimit` on the base (whole item
  type) or on a generated instance, and/or roll modifiers that add the `ModifierLimit`
  stat (see the `expanded` prefix). Effective limit = base + bonuses. **There is no
  universal cap anywhere in the engine** — the F1 debug command "Give 10-Modifier Item"
  demonstrates an item with 10 affixes and a limit of 12+.

## 11. Debug tools (F1)

Spawn enemy · give random rare mace/staff/equipment · give 10-modifier item · give Skill
Scroll · grant skill/character XP · kill nearby enemies · full heal — plus live FPS,
network mode/status, **ping**, connected player IDs, entity counts, position and
computed stats. All commands execute server-side like any other request.

## 12. Testing

- `dotnet run -- --nettest` — the automated two-client sync test described above
  (36 checks, exit code 0 on success). It exercises `127.0.0.1`; LAN/ZeroTier use the
  identical socket path with a different address.
- Manual: run two instances on one machine — instance A "Host Game" on 7777, instance B
  "Join Game" → `127.0.0.1:7777`.

## 13. Known limitations

- Placeholder shapes/labels instead of sprites; no animations or audio.
- Off-hand slot exists but no off-hand item types (shields) are defined yet.
- Client-predicted movement is only sanity-clamped, not fully server-simulated.
- Character save is written by the owning client (host doesn't persist other players).
- No NAT traversal: internet play requires port forwarding or a virtual LAN
  (ZeroTier/Meshnet/Tailscale — these work out of the box).
- Skill/character XP curves, enemy stats and drop rates are prototype-tuned, not balanced.
- One map layout algorithm (seeded arena); no zones/waypoints.

## 14. Recommended next steps

1. Sprite/animation pipeline (still runtime-loaded, e.g. free isometric packs) + audio.
2. Server-simulated movement with client reconciliation; interest management.
3. More archetypes (beams, chains, minions), support-scroll interactions (e.g. scrolls
   that modify other scrolls), unique items with fixed modifier sets.
4. Persistent host-side world/stash; trading between players.
5. Death penalties, potions/flasks, town/hideout area, waypoint travel between maps.
6. Lobby listing over LAN broadcast (still engine-free).
