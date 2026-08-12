# Scrollbound — Isometric Multiplayer ARPG Prototype

A playable isometric action-RPG prototype written **in plain C# with MonoGame** (no game
engine/editor): WASD movement, rebindable controls, real-time combat with dodge, blocking
(shields) and knockback, host-authoritative direct-IP multiplayer, grid inventory, equipment
with one/two-handed weapon rules, randomized
prefix/suffix loot with **per-item rolled prefix/suffix slot caps**, gold drops with
modifier-based item values, **Enchanting Scrolls** (stackable PoE-orb-style crafting
currency — right-click a scroll, then click an item to apply it), learnable skills
with levels, Skill Scrolls, a mana system with level-based regeneration, a character
sheet with per-damage-type DPS, procedural pixel-art sprites, data-driven JSON content
and local JSON saves.

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
100+ synchronization checks (join, movement, combat, enemy death, host loot generation,
identical modifiers on both peers, exclusive pickup, equipping, hand rules, blocking,
scroll attachment, enchant crafting, disconnect resilience). Exit code 0 = all passed.

## 4. Hosting a game

1. Launch → **Host Game**.
2. Enter your player name and a port (**default 7777**).
3. **Start Hosting**. The server listens on **0.0.0.0** (all interfaces), so LAN peers and
   virtual-LAN peers (ZeroTier, NordVPN Meshnet, Tailscale, …) can connect with no extra
   configuration. The host plays as a normal client connected over loopback.

**Single Player** is exactly the same thing minus remote players: it starts a local server
on a loopback OS-assigned port and connects to it, so single player and multiplayer share
one authoritative simulation path.

The server simulation runs on its **own dedicated thread** with a fixed 60 Hz timestep
(accumulator pattern with a stall guard), fully decoupled from the render loop — a frame
hitch on the host never stalls the world for remote players. The render thread never
touches server state directly; the hosting player's client talks to it over loopback UDP
like any remote client. `GameServer.Update` stays public so the headless test drives the
loop deterministically, and a standalone dedicated-server executable could reuse the same
class with its own loop. UI text is rasterized at its final on-screen pixel size (the
font atlas re-rasterizes whenever the UI scale changes), so menu/HUD text stays sharp at
1080p, 1440p, 4K and fractional scales.

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
The Options screen (main menu, or Pause → Options in game) is organized into tabs:
**Display** (borderless fullscreen + selectable resolution — menus and HUD scale
automatically with resolution via a global UI scale), **Gameplay** (persistent
**Damage Numbers**, **Enemy Health Bars** and **Player List & Pings** toggles) and
**Controls** (rebinding). In-game panels (inventory, skills, character sheet) also
have mouse "X" close buttons. In multiplayer, the player list (bottom left) shows
every connected player's round-trip ping, measured server-side and broadcast to all.

**Mana**: every skill except Basic Strike costs mana (shown in the Skill Menu). The
mana pool and its regeneration grow with character level (a PoE-style skill point
system will layer on top later), and items can roll **Sapphire** (+max mana) and
**of Clarity** (+% mana regen) modifiers. Costs are validated and spent server-side;
the blue orb (bottom right) tracks it. Melee strikes play a weapon swing animation
on the caster's held weapon. Arcane damage now has its own resistance plus melee
("Occult", mace-only) and spell ("Eldritch", staff-only) added-damage prefix
families. F1 → "Drop All Scrolls" drops one of every crafting and skill scroll.

**Dodge** base distance/duration/cooldown/i-frame duration are configured in
`Data/Config/dodge.json` and routed through the stat system (`DodgeDistance`,
`DodgeDuration`, `DodgeCooldownRecovery`, `DodgeInvulnerability` stats — equipment
modifiers can scale them). The cooldown and invulnerability are enforced
server-side; the dash movement is client-predicted for responsiveness. Damage is
shown via a networked damage-event system: the server broadcasts every damage
application and clients render floating combat numbers from those events.

**Weapons & shields**: every weapon is one- or two-handed (data-driven `TwoHanded`
flag — maces are one-handed, staffs two-handed). Shields are one-handed and fit
**either** hand, so mace + shield, shield + shield, or a lone two-hander are all
valid; equipping a two-handed weapon auto-unequips the off-hand to the bag, and the
off-hand refuses items while a two-hander is held. Held weapons AND shields orbit
the character toward the aim — with both hands full they spread apart like they're
actually held in each hand. **Blocking**: shields grant Block Chance (a % chance to
fully avoid one hit, capped at 75%); after a block it recovers for 2 seconds (base),
shortened by Block Cooldown Recovery modifiers. Blocks are rolled server-side and
show as a floating "Blocked" message. Shield Bash (a learnable skill) requires a
shield equipped in either hand: it lunges the caster forward (client-predicted,
with brief server-granted i-frames so the body-check can't hurt you), knocks the
target far back with a high chance (80%) to momentarily stun it, and gains +1
damage per 10 points of equipped shield armor. Enemy debuffs (stun, burning)
replicate as snapshot flags and render as tiny per-debuff icons above the enemy's
head.

**Skills**: *Mace Strike* (fast single-target swing with knockback, free), *Heavy
Strike* (area blow at the aimed point), Ground Slam, Shield Bash, Fire Bolt,
Arcane Burst, *Ice Spike* (a cold projectile with its own crystalline shard
sprite) and *Chain Lightning* (an instant bolt that strikes the enemy nearest the
aim and leaps between nearby targets — the exact chain path is broadcast so every
client draws the same jagged, flickering bolt; Multishot scrolls add extra jumps).
Melee strikes animate the actual held weapon sweeping an arc. Enemies softly
collide with each other, so packs spread out instead of stacking into one sprite.
In borderless fullscreen the game always renders at the desktop resolution (the
resolution setting applies to windowed mode), which keeps menus centered and the
mouse in sync.
**Critical hits**: every hit has a base 5% chance to crit for 150% damage;
*of Precision* (crit chance) and *of Ferocity* (crit damage) suffixes roll on
weapons only. Attack Speed suffixes roll on melee weapons, Cast Speed suffixes on
staffs — never crossed. Defense modifiers (armor, resistances) no longer roll on
weapons. Gold is picked up automatically by walking over it. Item tooltips show
armor and weapon damage pre-calculated (base + rolled modifiers combined).

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
├── World/                GameMap: seeded generation, layered terrain + collision
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

### Layered terrain (elevation, ramps, bridges)

The world is still fundamentally 2D/isometric — no voxels — but every tile column in
`GameMap` stores four bytes: **GroundLevel** (walkable elevation), **WallHeight**
(solid obstacle rising above ground; tall cliffs vary per column), **Ramp** (the
ground surface slopes up one level toward a direction — the only elevation
transition), and **BridgeLevel** (a second walkable deck above the ground, so one
entity can cross the bridge while another walks underneath at the same X/Y).

Entities carry one continuous `Height` (in level units) next to their X/Y. Movement
resolves against the **nearest reachable surface** within a step tolerance
(`GameMap.SampleHeight`), so walking up a ramp raises the height smoothly and which
layer an entity occupies simply falls out of its height — there is no explicit layer
switch anywhere. Combat, AI aggro, AoEs, pickups and knockback all filter targets by
`|Δheight| ≤ 0.75`, which is what isolates a bridge deck from the passage below.
Projectile/line-of-sight checks (`SegmentBlocked`) block on walls and on terrain
rising above the flight height, so cliffs stop ground-level shots while defenders on
the plateau can fire down. Heights ride along in every movement/spawn/effect packet
(protocol v8) and the renderer offsets sprites by `24 px * height` with painter's
depth `x + y + height * 0.6`, which draws bridge decks over the entities beneath.

Generation stays simple: the seeded arena plus a deterministic, seed-independent demo
carve (`CarveDemoTerrain`) with two plateau levels, a tall cliff wall, transitions
inset into the cliff edges (a smooth ramp AND a stairs variant — the same tile data
with a per-tile render style), and a bridge over a walkable corridor — the
scaffolding a real generator can later replace tile by tile.

Terrain renders from runtime-baked isometric prism sprites (`TextureGen`): each
wall/cliff column draws a top diamond plus two sheared side faces whose edges match
the diamond geometry exactly, so silhouettes are straight instead of jagged; ramps
and stairs are baked per ascent direction with a genuinely sloped (or stepped) top
surface. Side faces sort as occluders (`x+y+1`) while walkable tops sort low
(`x+y+level*0.6`), which is what lets tall towers stand behind short ones without
paint-over glitches while entities still draw above their own floor.

### Enemy pathfinding (flow fields)

Enemy aggro and chasing run on a per-player breadth-first **flow field** over the
walkable-surface graph: nodes are (tile, surface) pairs — bridge tiles contribute a
ground node and a deck node — and edges connect surfaces whose heights meet within
the step tolerance. Aggro triggers on *path* distance, so climbing a ramp no longer
breaks aggro: enemies route to the ramp or stairs and follow you up (and around
pillar walls on flat ground). Attacks remain strictly same-surface, so nothing hits
through a cliff face or a bridge deck. Fields recompute a few times a second per
player (~4k nodes), cheap enough for much larger generated maps later.

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
- One map layout algorithm (seeded arena + fixed terrain demo); no zones/waypoints.

## 14. Recommended next steps

1. Sprite/animation pipeline (still runtime-loaded, e.g. free isometric packs) + audio.
2. Server-simulated movement with client reconciliation; interest management.
3. More archetypes (beams, chains, minions), support-scroll interactions (e.g. scrolls
   that modify other scrolls), unique items with fixed modifier sets.
4. Persistent host-side world/stash; trading between players.
5. Death penalties, potions/flasks, town/hideout area, waypoint travel between maps.
6. Lobby listing over LAN broadcast (still engine-free).
