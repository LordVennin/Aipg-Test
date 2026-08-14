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
| Passive Skill Tree | `P` |
| Command summons: TAP to rally the focused pack at the cursor, HOLD to order it back to following (aiming near yourself also recalls) | `` ` `` (backquote) |
| Cycle summon focus (which pack the command key drives) | `Tab` |
| Interact: talk to NPCs / pick up items | `F` (or click an item's ground label) |
| Debug menu | `F1` |
| Pause / close panels / menu | `Escape` |

Bindings are saved to `Saves/settings.json` next to the executable and restored on start.
The Options screen (main menu, or Pause → Options in game) is organized into tabs:
**Display** (borderless fullscreen + selectable resolution — menus and HUD scale
automatically with resolution via a global UI scale), **Gameplay** (persistent
**Damage Numbers**, **Enemy Health Bars** and **Player List & Pings** toggles) and
**Controls** (rebinding — the binding list is taller than the panel now, so it
scrolls with the mouse wheel instead of overlapping the Back button). In-game
panels (inventory, skills, character sheet) also
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

**Skills**: *Mace Strike* (fast swing that hits EVERY enemy in its arc with light
knockback, free — no impact circle, the weapon swing IS the visual), *Mace Slam*
(mace-only ground slam at the aimed point: a 0.35s WIND-UP before the hit lands —
the overhead animation stretches to match, and the hit resolves from wherever the
caster IS at landing time, so moving mid-swing moves the impact and its visuals
with you — then heavy knockback, a 60% chance to Slow survivors for 2.5s, a
cracked-earth impact overlay and a burst of dust and terrain-colored rock debris —
tagged `Slam`, higher mana cost),
Ground Slam, Shield Bash, Fire Bolt, Arcane Burst, *Ice Spike* (a cold projectile
with its own crystalline shard sprite) and *Chain Lightning* (an instant blue-white
bolt that strikes the enemy nearest the aim and leaps between nearby targets — the
exact chain path is broadcast so every client draws the same jagged, flickering
bolt; Multishot scrolls add extra jumps; the tan-gold palette is reserved for
future light damage). Melee strikes animate the actual held weapon sweeping an arc.

**Attack damage**: attack skills deal a PERCENT of your equipped weapon's damage
(PoE2-style — shown as "N% of weapon" in the Skill Menu), never flat skill damage
on top; spells keep their own base-damage progression, and Shield Bash adds its
shield-armor bonus on top of its weapon percent.

**Ailments**: hits can inflict scaling status effects, all server-authoritative and
replicated as debuff flags (tiny icons above heads plus on-body visuals):
- **Chill / Freeze** (Ice Spike): chilling hits build a 0-100% chill magnitude from
  the hit's damage relative to the target's max life, scaled by the skill's and the
  player's chill magnitude increases. Chill decays constantly and slows movement up
  to 50%; at 100% every further chilling hit can FREEZE the target solid — frozen
  enemies and players tint blue and can't act.
- **Electrocute** (Chain Lightning): 6 seconds of live current — every 2 seconds
  the victim can seize up, frozen in place with a crackle of electricity. Works on
  players too.
- **Ignite** (Fire Bolt base chance, Burning scroll adds more): a fire DoT worth
  80% of the igniting hit over 4s, scaled by ignite magnitudes, with rising flame
  visuals.
- **Poison** (Venom scroll, melee): a DoT off the physical + dark + acid portions
  of the hit (60% over 4s), scaled by poison magnitude — green bubbles.
- **Bleed** (Rending scroll, melee): physical-only but heavier (90% over 4s) —
  dripping red.

**New Skill Scrolls**: *Venom* and *Rending* (melee poison/bleed chance), *Frenzy*
(20% increased melee attack speed), *Shattering* (Cold projectiles burst into 5
small ice shards behind the struck enemy at 20% damage — a unique shard sprite,
and added-projectile scrolls deliberately can't raise the count) and *Scorched
Earth* (Fire projectiles leave a 3-second burning ground circle that ticks fire
damage and shreds fire resistance by a stacking 1% per second — 5s stacks, up to
25%).

**Skill leveling is manual**: skill XP banks up as you fight, but skills no longer
level automatically — when a skill has enough banked XP, a **Level Up** button
appears in its Skill Menu (`K`) detail pane, so you decide when (and whether) a
skill advances instead of over-leveling it by accident. The server validates the
spend like any other request.

**Summons**: learnable `Summon`-archetype skills that are NOT cast from the
hotbar. In the Skill Menu (`K`), summon skills show **`+` and `−` buttons** (in
the list row and the detail pane) to raise or dismiss minions. Each summon costs
**flat mana + 5% of your max mana** (the flat part grows per skill level while
the minions' health and damage scale up), with a limit of 2 per skill at level 1.
Two summon skills exist so the command systems have something to differentiate:
- **Skeleton Archers** (10 base mana): hold position and shoot arrows at the
  nearest enemy with a clear line of fire (kill credit and XP go to the summoner).
- **Skeleton Warriors** (12 base mana, tougher, harder-hitting, slower respawn):
  charge enemies and cut them down at arm's reach — a distinct sprite with a
  notched sword and scrap shield.

Minions have health bars and take damage — enemies turn on them and enemy
projectiles hit them first — and a dead minion **respawns free** near the
summoner after its skill's respawn time. They're animated from `SummonAttack`
events: warriors carry a separately-drawn bone sword (lowered at rest, chopped
through the strike direction with a forward lurch on every swing), archers
recoil from each bow release, and both bob lightly while walking instead of
gliding as static sprites. Summons softly collide with each other
(the same push-apart enemies use), so packs fan out instead of stacking into one
sprite. A **summon roster** sits beside the mana orb: one card per learned summon
skill with its living count / limit; `Tab` cycles which pack is FOCUSED (lit
border). The backquote key (`` ` ``) commands the focused pack: **tap** to rally
it at the cursor — rallied minions march to the point before picking fights,
then hold it (rallied warriors still chase prey near their post, or they could
never swing) — and **hold** the key to order it back to following you. Because
rallies are stored PER SKILL server-side, archers can hold a ridge while the
warriors hold a doorway. Three gear modifiers feed all summons through the
regular stat pipeline: **Commanding** (% summon damage, prefix, helmets +
staffs), **of Undeath** (% summon health, suffix, helmets + staffs) and **of
Legions** (+1/+2 summon limit, suffix, helmets only).

**Use time (global cast lockout)**: every skill declares a `UseTime` — a global
lockout (enforced server-side, mirrored client-side on the hotbar) that stops the
whole hotbar from being dumped in a single instant: casting anything locks ALL
skills for that skill's use time, on top of per-skill cooldowns.

**Charged Shield Bash**: Shield Bash is `Chargeable` — hold the button to charge
up to ~1s (a charge bar fills above the hotbar) and release to lunge farther
(up to +80%) and hit harder (up to +70% damage/knockback). A quick tap still
fires a normal bash. Enemies softly
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
- **Attack FEEDBACK is client-predicted too** (the PoE approach): without it, a remote
  player's swing would only start once the server's effect broadcast made the full
  round trip back — ~170ms of dead time per click at 150 ping, while movement feels
  instant. On a cast request the casting client immediately plays its own swing or
  wind-up animation (the server's echo of that cast is suppressed so it doesn't play
  twice), and projectile skills loose a cosmetic **ghost bolt** on click that the
  authoritative projectile ADOPTS on arrival — it inherits the ghost's flight progress
  so nothing snaps backwards. Damage, hits, cooldown enforcement and what every OTHER
  player sees remain fully server-side; a rejected cast just shows a swing that hits
  nothing, and an unconfirmed ghost fizzles within a second.
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

**Water** is a second kind of impassable tile: unlike walls it has no height, so
nothing can WALK onto it, but shots, sight lines and effects pass freely over the
surface (and a bridge deck above water stays walkable). The generator floods a few
noisy-ellipse ponds on open level-0 ground from their own seeded stream — never in
the authored demo region, near the player spawn, or under walls/ramps/bridges/
features — and enemy spawn points, dirt paths, clutter and the pathfinding flow
field all steer around them. Ponds render as depth-shaded blue with shore foam on
land edges and slow drifting glints.

Terrain renders from runtime-baked isometric prism sprites (`TextureGen`): each
wall/cliff column draws a top diamond plus two sheared side faces whose edges match
the diamond geometry exactly, so silhouettes are straight instead of jagged; ramps
and stairs are baked per ascent direction with a genuinely sloped (or stepped) top
surface. Side faces sort as occluders (`x+y+1`) while walkable tops sort low
(`x+y+level*0.6`), which is what lets tall towers stand behind short ones without
paint-over glitches while entities still draw above their own floor.

### The graveyard slice (authored encounters)

The map stages a hand-authored 5–10 minute run over the demo terrain: pack
spawners (groups that share aggro and respawn together) guard the lower path,
an ambush waits under the bridge, elite-led packs hold the upper ruins, spitters
overlook the corridor from the plateau rim, and **The Gravelord** — a ground-slam
miniboss with stun resistance — waits with its guards in the wide arena on the
south plateau, dropping a guaranteed reward burst (rare-biased loot plus both
scroll types, twice) from its own loot table. Elite affixes (Brutish/Swift/
Warded) scale life/damage/speed/resists, multiply XP and loot rolls, tint the
sprite and prefix the name.

### Overlook combat & hover targeting

Ranged enemies can fire ACROSS elevations when they have line of fire: shots are
straight lines in (x, y, height) space (`GameMap.ShotBlocked`), so rim spitters
rain projectiles down into the corridor while shots from below clip the cliff
lip and die — holding the high ground is a real advantage. Nothing shoots
through a bridge deck's plane in either direction. Projectiles carry a height
step and arc to their target's elevation.

Hovering an enemy draws a red OUTLINE around its sprite (a silhouette rim — the
sprite itself stays untinted) and shows a top-of-screen target display (name
colored by rank, large health bar). Casts made while hovering are TARGETED: the
server aims at that enemy's true position and elevation, which is how you pick a
victim above or below you — the mouse unprojection alone cannot know which
surface you meant.

**Ground loot UX**: item name labels that would overlap stack into a tidy
vertical list per cluster. Hovering a label highlights it; the pickup key then
grabs THAT item — auto-walking you to it first when it's out of reach (any
manual movement cancels the walk). Clicking a label works too.

### Zone themes

Zones have data-driven identities (`Data/Zones/themes.json`) that are decided
BEFORE the map generates and replicated to joining clients (the theme id rides in
JoinAccept next to the seed). A theme carries the full terrain palette (floors,
cliffs, walls, ramps, bridge, background), a clutter style and density, a chance
for 1-level wall tiles to render as themed FEATURES on their base block (crypts,
columns, rock spires, small trees) — and it can SHAPE GENERATION itself: the
forest grows large multi-tile trees, generated as solid 2x2 two-level wall
columns from their own seeded stream (base layout stays identical across themes
for a given seed), so collision, pathfinding avoidance and line-of-sight blocking
come free while the renderer draws one big canopy sprite per tree. Forest ground
uses an ORGANIC floor: per-tile shades hashed from the seed on gridless tiles
with speckles, replacing the checkerboard. Clutter is non-colliding decoration
laid out deterministically from the seed (identical on every client). Four themes
ship: **Forsaken Graveyard** (default), **Sunken Tomb**, **Scorched Bluffs** and
**Mirewood**. The hosted zone is chosen in Options → Gameplay ("Zone: ..."), or
forced with the `ARPG_THEME` environment variable; the planned hub/teleporter
will assign one per destination zone. **Mirewood (forest) is the default zone.**

### The merchant (test shop)

**Weaver the Peddler** sets up camp a few tiles from the player spawn. Walk up
and press the pickup key (`F`) to talk — a DIALOGUE panel opens first (random
flavor line plus options; browsing the shop is one of them). The shop shows the
stock as an inventory-style grid — the same cells, sprites and tooltips as your
bag — and opens your inventory beside it in SELL MODE, where clicking a bag item
sells it. The stock is PER PLAYER and deterministic: seeded from (character name,
character level), so every player sees their own six items (the last is always a
rare), the shop rerolls exactly once per level-up, and leaving/rejoining a
session never rerolls it — no shop-scumming by hopping between friends' games.
Purchases mark the slot SOLD for the rest of the level (persisted on the
character save). Buy price is twice the item's gold value; selling an inventory
item (click it in the right column) pays its base value. All transactions are
validated server-side like any other request.

### Passive skill tree

`P` opens a PoE-style passive tree. One point per character level past the first;
nodes allocate outward from the start node ("Adventurer's Spark"), each adding
stats through the SAME StatCollection pipeline as item modifiers — any existing
stat works as a perk with zero extra code. Allocation is server-validated
(existence, adjacency, unspent points) and persists in the character save. The
starter cluster is deliberately tiny (10 perks in three branches: melee, defense,
caster) — the SYSTEM is the point, and class-specific starting trees can replace
`Data/SkillTree/tree.json` later without code changes.

### Enemy pathfinding (flow fields)

Enemy aggro and chasing run on a per-player breadth-first **flow field** over the
walkable-surface graph: nodes are (tile, surface) pairs — bridge tiles contribute a
ground node and a deck node — and edges connect surfaces whose heights meet within
the step tolerance. Aggro triggers on *path* distance, so climbing a ramp no longer
breaks aggro: enemies route to the ramp or stairs and follow you up (and around
pillar walls on flat ground). Attacks remain strictly same-surface, so nothing hits
through a cliff face or a bridge deck. Fields recompute a few times a second per
player (~4k nodes), cheap enough for much larger generated maps later.

### Telegraphed enemy melee (wind-up → whiff or hit → recovery)

Melee enemies no longer deal instant "contact" damage. When an attack comes off
cooldown the enemy COMMITS to a swing: it stops, locks a strike direction, and
winds up for a data-driven `AttackWindup` (~0.35-0.55s). Only when the wind-up
lands does the server re-check reach and the swing's `AttackArc` — if you dashed
or strafed out (or your dodge i-frames cover the impact), the attack **whiffs**
outright. A short `AttackRecovery` pause after every swing leaves a punish
window, attack cooldowns get ±12% jitter so packs don't swing in unison, and a
stun or freeze mid-wind-up cancels the swing entirely (the cooldown stays spent,
so interrupts genuinely deny hits). Enemies attacking your summons use the same
telegraph. The commitment style is per-enemy (`AttackStyle`):
- **"lunge"** (zombies, the boss): the direction LOCKS at wind-up start —
  strafing beats them; whiffing is their character.
- **"sword"** (the new **Barrow Knight**, an armored skeleton with its own
  Skeleton sprite style and a drawn bone blade): tracks its victim until just
  before impact — only a real dash escapes it.

Clients animate from `EnemyAttack` events (phase 1 = wind-up, phase 2 = swing):
the body leans back through the wind-up and flashes brighter in its last stretch
as the final tell, then lurches along the strike. Lunge attackers rake glowing
claw streaks; sword knights carry a visible blade that rests at their side,
raises behind the shoulder during the wind-up, and chops through the strike
direction — all transform-based on the procedural sprites, no new frames.

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
  (`MeleeStrike` / `MeleeArea` / `Projectile` / `AreaBurst` / `Summon`), tags,
  damage/scaling fields. A genuinely new behavior means one new archetype case in
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

Dev conveniences for automated/headless sessions: `--sp` starts straight into
single player, `ARPG_THEME=<id>` forces the hosted zone theme, and
`ARPG_DEVUI=debug[,skills][,inventory][,drops][,shop][,shopgrid][,tree][,summons][,knight]`
opens panels at startup (`drops` scatters one of every scroll shortly after
joining, for loot-UI work; `shop` opens the merchant shop without needing
keyboard input; `summons` learns the summon skills and raises a pack; `knight`
spawns a Barrow Knight beside the player for attack-animation work).

## 12. Testing

- `dotnet run -- --nettest` — the automated two-client sync test described above
  (304 checks, exit code 0 on success). It exercises `127.0.0.1`; LAN/ZeroTier use the
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
