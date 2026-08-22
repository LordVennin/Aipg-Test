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

### Troubleshooting: "ping works, but the game can't connect" (Meshnet / ZeroTier / Tailscale)

Ping is ICMP; the game is **UDP** — a firewall can pass one and drop the other. If the
join screen ends with *"the host never answered (UDP)"*, work down this list on the
**HOST's** machine (the joiner's firewall almost never matters — outbound UDP is allowed
by default):

1. **Windows Firewall is the usual culprit.** VPN adapters (Meshnet, ZeroTier, Tailscale)
   are typically classified as **Public** networks, and the first-run "allow access"
   prompt only ticks *Private* by default — so the game is silently blocked exactly on the
   VPN interface. Fix: Windows Security → Firewall & network protection → *Allow an app
   through firewall* → find the game (or add it) and tick **Public** too. Or add a port
   rule as admin:
   `netsh advfirewall firewall add rule name="Scrollbound" dir=in action=allow protocol=UDP localport=7777`
2. **Use the host's VPN IP.** The Host screen lists every address the machine has (LAN
   and VPN) — for NordVPN Meshnet share the `100.x.y.z` one (or the `*.nord` hostname;
   both work). The joiner enters it with port `7777`.
3. **Meshnet permissions**: in the host's NordVPN app, the joiner's device needs
   *"Allow incoming connections"* enabled (per-device toggle in the Meshnet device list).
4. **Same build on both machines.** The protocol version is checked on join — mismatched
   builds get an explicit *"Version mismatch (server X, you Y)"* message rather than a
   silent failure, so if you see THAT, update the older copy.
5. Third-party antivirus firewalls (Bitdefender, Kaspersky, …) have their own allow
   lists — the Windows one being open isn't enough if one of these is installed.

Quick isolation test: on the host run **Host Game**; on the SAME machine run a second
copy and join `127.0.0.1:7777` — if that works (it should), the game is fine and the
block is between the machines.

**Built-in UDP path tester** — proves where packets die without touching the game
protocol (console-only, works on Windows and Linux):

```
# on the HOST machine:
dotnet run -- --udpecho            # listens on UDP 7777 and echoes everything back

# on the JOINING machine:
dotnet run -- --udpping 100.x.y.z  # the host's Meshnet/LAN IP
```

Replies flowing both ways = the path is open and the game will connect. Silence =
the drop is in a firewall or VPN permission, not the game. Note that an app-specific
**Block** rule (created if the first-run firewall prompt was ever cancelled) overrides
any port-allow rule — check *Windows Defender Firewall with Advanced Security →
Inbound Rules* for red "blocked" entries naming the game or `dotnet`, delete them,
then re-host and accept the prompt with BOTH network types ticked. Beware that
`--udpecho`/`--udpping` run under the same executable, so they test the same
firewall rules the real game hits.

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
| Interact: doors (ready up) / chests / NPCs / pick up items | `F` (hover a ground label to target a specific drop) |
| Health flask / Mana flask (equipped items; restore over time; fountain refills) | `Q` / `E` |
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
the blue orb (bottom right) tracks it. A cast you can't afford shows NOTHING — no
swing animation, no ghost bolt, no cooldown started, just a "Not enough mana"
reminder — and instant-target skills (Chain Lightning) aimed at empty air fizzle
for FREE on both sides: no mana, no cooldown. Melee strikes play a weapon swing animation
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

**Skills**: *Mace Strike* (fast swing that hits EVERY enemy in a player-centered
frontal sweep matching the visible swing (a hair past 180°, with a little reach
forgiveness) — point-blank enemies are always caught, and the swing
can never reach past range+bodies — with light knockback, free; no impact circle,
the weapon swing IS the visual), *Mace Slam*
(mace-only ground slam at the aimed point: a 0.35s WIND-UP before the hit lands —
the overhead animation stretches to match, and the hit resolves from wherever the
caster IS at landing time, so moving mid-swing moves the impact and its visuals
with you — then heavy knockback, a 60% chance to Slow survivors for 2.5s, a
cracked-earth impact overlay and a burst of dust and terrain-colored rock debris —
tagged `Slam`, higher mana cost),
*Ground Slam* (self-centered AoE: a 0.4s overhead wind-up, then the earth CRACKS —
radiating fissures plus a storm of dust and debris around the caster — with
knockback, a brief stun, 12 mana and a long 2.2s cooldown),
Shield Bash, Fire Bolt, Arcane Burst, *Ice Spike* (a cold projectile
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
- **DoT stacking**: bleed and poison apply as STACKS, each with its own tick
  rate and timer. One skill keeps 1 stack on an enemy by default; each attached
  Rending/Venom scroll raises that skill's cap by +1, and stacks from different
  skills or different players always coexist. When a source is at its cap only
  the strongest instances survive — a stronger hit replaces the weakest stack,
  a weaker one merely refreshes it. Total DoT = the sum of every live stack.
- The chill icon above an enemy shows the buildup toward the freeze: a fill
  bar on the icon plus the exact percent — freezes trigger at 100% (2.2s base
  freeze).

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
the list row and the detail pane) to raise or dismiss minions. Each summon **RESERVES
flat mana + 5% of your maximum mana** while it exists (PoE-style — the reserved
band shows dimmed on the mana orb and is released when you dismiss the minion;
the flat part grows per skill level while the minions' health and damage scale
up), with a limit of 2 per skill at level 1.
Two summon skills exist so the command systems have something to differentiate:
- **Skeleton Archers** (reserve 10 base mana): hold position and shoot arrows at the
  nearest enemy with a clear line of fire (kill credit and XP go to the summoner).
- **Skeleton Warriors** (reserve 12 base mana, tougher, harder-hitting, slower respawn):
  charge enemies and cut them down at arm's reach — a distinct sprite with a
  notched sword and scrap shield.

Minions have health bars and take damage — enemies turn on them and enemy
projectiles hit them first — and a dead minion **respawns free** near the
summoner after its skill's respawn time (its mana stays reserved while it waits). They're animated from `SummonAttack`
events: warriors carry a separately-drawn bone sword (lowered at rest, chopped
through the strike direction with a forward lurch on every swing), archers
recoil from each bow release, and both bob lightly while walking instead of
gliding as static sprites. Summons softly collide with each other
(the same push-apart enemies use), so packs fan out instead of stacking into one
sprite. A **summon roster** sits beside the mana orb: one card per summon skill
with at least one LIVING minion (living count / limit) — merely learning a summon
skill shows nothing, so melee characters never see the element; `Tab` cycles
which pack is FOCUSED (lit border). The backquote key (`` ` ``) commands the focused pack: **tap** to rally
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
  so nothing snaps backwards. If the ghost already FINISHED before confirmation
  (it stopped on an enemy, or capped out) its final progress is remembered for a
  moment and the real bolt fast-forwards to where it ended — without that, every
  close-range cast at high ping visibly "fired twice" as the real bolt re-flew
  the whole path. Damage, hits, cooldown enforcement and what every OTHER
  player sees remain fully server-side; a rejected cast just shows a swing that hits
  nothing, and an unconfirmed ghost fizzles within a second. Ghosts never spawn for
  casts the client already knows will fail (no mana), and they stop on the first
  enemy they visually touch — while server-side projectile hits sweep each tick's
  full travel segment, so even very fast bolts can never step across a body and
  "pass through" it.
- **Delivery**: player/enemy position snapshots go unreliable at 20/10 Hz (newest wins);
  everything that matters (joins, spawns, deaths, health, loot, character state) goes
  `ReliableOrdered`.
- **Single player = the same stack**: local server + loopback client, so there is exactly
  one gameplay code path.
- The map is generated deterministically from a seed the server sends on join, so only
  the seed crosses the wire.

### The campaign loop (hub → three runs → boss → home)

Hosted games now run the actual GAME LOOP instead of the old test arena
(set `ARPG_ARENA=1` to get the arena back for debugging):

- **The Sanctum (hub)**: everyone starts in a small square room holding **two
  merchants** — Weaver the Peddler (gear, the existing shop) and **Maren the
  Lorekeeper**, the skill trainer — plus **four starter chests** (press `F` to pop
  one; two plain level-1 items drop out; each chest opens once per session) and the
  **run door** on the east wall. The hub renders in its own **sanctum theme**:
  purple stone-slab floors with mortar seams and brick-coursed purple walls
  (`StoneBrick` themes bake masonry into the floor diamonds and wall prism faces),
  while run maps keep the campaign's zone theme — the map packets carry each MAP's
  theme id, so clients swap automatically at the door.
- **The stash**: a banded chest by the hub's north wall — press `F` beside it to
  open your storage (the bag opens alongside for dragging). Storage is tied to
  the CONTAINER object, not a global array: the character holds one 10x8 grid
  per container id (`hub_stash` today; future player rooms add more), every
  move is server-validated with a reach check, and contents persist on the save.
- **Death & revival**: dying in the campaign leaves a corpse — no auto-respawn.
  A living teammate revives you by standing at the corpse and **holding `F`**
  for 2.5 seconds (server-timed, with a progress bar over the body; letting go
  bleeds the channel back down). You stand back up in place at half health. If
  the WHOLE party goes down, the run ends: the Sanctum reclaims everyone alive
  at full health, and coming home through the door also revives any dead.
- **Bows & quivers**: a second weapon class — bows are two-handed ranged
  weapons that uniquely share with a **quiver** in the off-hand (implicit
  attack speed by tier). **Arrow Shot** (from the trainer) is a zero-mana
  weapon-damage arrow — a bow build's bread-and-butter. Bow attacks deal
  Thrust; three bow and quiver tiers drop as loot at levels 1/15/35.
- **New undead**: **Crypt Leapers** (loop 1+) telegraph a ground line, then
  leap it; **Grave Callers** (loop 2+) raise runty Shamblers mid-fight and
  drop purple-telegraphed dark AoE circles that lock at cast start — step out
  before they fill.
- **Ready doors**: stand at a door and press `F` to toggle READY (shown over the
  door and in chat). The group transitions only when EVERY living player is ready —
  nobody gets left behind in multiplayer. Transitions rebuild the world server-side
  and broadcast a `MapChange`; clients regenerate the same map from the seed, wipe
  replicated state and receive a fresh snapshot on the same ordered channel.
- **Run maps**: each excursion is THREE generated hallway-style forest maps
  (96x26): a meandering corridor with real terrain — terrace bands crossing the
  hall (stairs only near the corridor line, cliffs elsewhere), overlook plateaus
  with spitter nests, pillar clusters, ponds and the theme's big trees. A
  ground-surface BFS validates spawn-to-exit walkability at generation (with a
  deterministic corridor-carve fallback). **Packs are placed at generation and
  never respawn** — a cleared map stays cleared; the enemy cap is 100.
- **Scaling**: excursion 1 runs at enemy level 1; every RETURN trip through the
  hub door generates three NEW maps and raises the enemy level by 3 (loop 2 = 4,
  loop 3 = 7, ...). **Graveguard skeletons** (Barrow Knights) join the pack mixes
  from the second loop — guaranteed, not dice.
- **Party XP**: the player who lands a kill earns its full XP; every other
  player in the game earns **70%** of it (each through their own under-level
  penalty), so a high-damage build sniping kills doesn't starve the group.
  Skill XP still follows the skill that struck the blow.
- **Forest dressing**: run maps grow **tall grass** — Pokemon-style patches
  (terrace tops included) whose front blades sway and draw OVER whatever stands
  in them, hiding the lower half of players and monsters alike; big trees come
  in four silhouettes (two broadleaf, a dark tiered fir, a pale birch), small
  wall-trees in three, and the clutter mix adds mossy boulders, lichen slabs
  and ferns. Elevated terrain uses the same mottled grass/dirt palette as the
  ground floor, and terrace tops carry trees and clutter like anywhere else.
- **The Gravelord's arena**: map 3 ends in a cleared pocket by the exit where the
  boss waits with guards. The exit door is SEALED (red glow) while he lives, and
  he now **summons three Grave Spitters** around himself on a long cooldown
  (first summon ~11s into the fight — never as an opener; adds spawn at his
  level). Kill him, the seal lifts, and the door leads back to the Sanctum.
  From the **second loop** he also gains a **dash charge**: he roots for a full
  second while a red MMO-style ground LINE burns along his locked path, then
  barrels down it for heavy contact damage — step off the line and it all
  misses. Stuns and freezes cancel the charge.
- **Potion flasks are ITEMS**: flasks drop as equippable gear (two dedicated
  flask slots in the inventory) with their own stats — how much they restore,
  over how long, and how many charges they hold. New characters start with a
  Minor pair (health on `Q`, mana on `E`); Greater flasks drop from level 14.
  Sips restore over time, never instantly, and the mana flask respects summon
  reservations. **Charges never regenerate** — refill at the Sanctum
  **fountain** (`F` beside the basin refills every flask you carry), and every
  flask starts a play session full. The HUD bottles beside the orbs show the
  equipped flasks' charges and pulse while a sip works.
- **No stranded terrain**: a post-generation connectivity pass BFS-checks every
  walkable tile from the spawn, carves extra stairs wherever a terrace or
  plateau pocket sits one clean level away, and turns any last unreachable
  sliver into scenery — stairs never lead to places you can't actually go. An
  **orphan-stair cleanup** also removes any staircase whose terrace got
  flattened by a later pass (boss arena, ponds, corridor carve) so stairs
  never sit embedded in flat ground climbing to nothing.

**Starter economy**: new characters begin with **100 gold** and only their
class kit's single skill. Every other skill is bought from the skill trainer
for **75 gold** (server-validated: in person, gold up front). The K menu lists
only learned skills and points at the trainer for the rest.

### Sound (drop-in WAV pipeline)

The audio stack is fully data-driven and works TODAY with **procedural
placeholder sounds** (synthesized at runtime, same philosophy as the sprites):
UI clicks, swings, slams, casts, arrows, hits, blocks, hurt/death, loot and
gold pickups, chests, doors, level-ups, flask sips, revives and enemy
telegraph warnings — all positioned in the world with distance falloff and
stereo pan against the local player, throttled per sound, with a master
volume in Options → Gameplay.

**To add real sounds:** drop WAV files (44.1kHz 16-bit PCM) into
`ARPG/Assets/Sounds/` using the names listed in that folder's README — each
file automatically replaces its placeholder, no code changes. The registry
(`Data/Sounds/sounds.json`) maps sound ids to files with per-sound volume,
pitch variance and spam throttles; add entries there for new sounds (e.g.
`cast_<skillId>.wav` for a per-skill cast, `die_<enemyId>.wav` for a
per-enemy death — the hooks already look those ids up first and fall back to
the generics). Machines without an audio device (and the headless test) run
silent with zero errors.

### Character creation, classes & body sprites

Starting any game (single player, hosting or joining) first stops at the
**character select screen**: every save on disk as a clickable row with a live
sprite preview (body + worn armor), name, level and class — pick one to play,
or delete with a two-stage confirm. **New Character** opens the **creation
screen**: pick a **class**, a
**body style**, a **hair style** (Short / Long / Bun / Bald), and BOTH colors —
skin and hair each offer quick-pick swatches **plus free RGB sliders**, so any
24-bit color goes (green skin, pink hair, whatever). A live animated preview
bakes the actual in-game sprite as the sliders move. The finished character is
saved and the interrupted action resumes automatically.

- **Classes are starting KITS, not restrictions** (`Data/Classes/classes.json`):
  **Warrior** (wooden club + Mace Strike), **Archer** (short bow + leather
  quiver + Arrow Shot) and **Mage** (oak staff + Fire Bolt). The class only
  decides what you stand up with on day one — items, skills and progression
  never check it again.
- **One human rig** (16x27, three frames: idle + two stride poses) drawn for
  two body styles — **male** (broad shoulders) and **female** (tapered waist) —
  with hair style and both colors fully independent of the body
  (`Sim.Appearance` holds the swatch palettes and the packed-RGB helpers).
  Because every body uses the same rig, future armor overlays draw once and
  fit everyone; other RACES are planned as their own standalone patch later.
- **4-way facing**: the body TURNS with the aim (mouse) direction — front,
  back, and a true side profile that mirrors for west. Every layer (hair,
  helmets, armor) is drawn for all three views, so a full helm shows its eye
  slit only from the front and long hair falls down the back when you turn
  away. Animation stays deliberately minimal: the walk cycle is the only
  body animation, and held weapons hover/point per the aim like always.
  Dev aid: `ARPG_DEVUI=face:N` (S/E/W) pins the facing for screenshot runs.
- **Appearance replicates**: body style, hair style, the exact skin/hair RGB
  values AND the five worn armor slots ride the `PlayerAppearance` packet
  (protocol v29), so every client renders every player's chosen look, walk
  animation, equipped weapons and worn armor byte-exact. Dead players render
  grey, frozen ones ice-blue — the tints apply to the body sprite. Pre-color
  saves still load: their stored preset index becomes the fallback color.
- **Worn armor draws on the body** ("paper doll" layers on the ONE rig):
  every body armor and helmet base declares an overlay silhouette
  (`ArmorStyle`) plus a garment color (`SpriteColor`) in `armor.json` —
  body armor styles **cloth** (a full robe that drapes over the legs),
  **leather** (stitched jerkin), **mail** (ring texture) and **plate**
  (pauldrons + chest ridge); helmet styles **hood**, **cowl**, **cap** and
  **helm** (full faceplate with an eye slit — helmets hide hair). The small
  slots stay lightweight: **gloves** recolor the hands, **boots** the feet,
  **belt** the waist line. Because everything paints over the same rig
  coordinates, every piece automatically fits both bodies and all future
  overlays. Dev aid: `ARPG_DEVUI=gear:plate` (or leather/hide/cloth/iron)
  wears a full set at boot for screenshot runs.

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
south plateau. Its slam is TELEGRAPHED: a pulsing red MMO-style decal marks the
full slam circle for 0.9s, then damage resolves against wherever everyone stands
at the end — walk out of the red and it misses (stuns/freezes cancel the slam
outright). It drops a guaranteed reward burst (rare-biased loot plus both
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
manual movement cancels the walk). Left-click never picks up items — the pickup
key is the only grab, so attacks can't eat loot clicks.

**Draggable windows**: every gameplay panel (inventory, skill menu, character
sheet, passive tree, shop grid) has a title-bar grip — drag it to rearrange your
screen (positions clamp so a window can never be lost off-screen). Panels are
z-ordered: the last one you click (or open) draws on top, and windows underneath
an overlap never react to clicks meant for the window above.

### Zone themes

Zones have data-driven identities (`Data/Zones/themes.json`) that are decided
BEFORE the map generates and replicated to joining clients (the theme id rides in
JoinAccept next to the seed). A theme carries the full terrain palette (floors,
cliffs, walls, ramps, bridge, background), a clutter style and density, a chance
for 1-level wall tiles to render as themed FEATURES on their base block (crypts,
columns, rock spires, small trees) — and it can SHAPE GENERATION itself: the
forest grows large trees from their own seeded stream (base layout stays
identical across themes for a given seed). Only each tree's TRUNK tile is solid
(one two-level column for collision, pathfinding avoidance and line-of-sight);
the rest of the 2x2 footprint is ordinary walkable ground the canopy sprite
merely overhangs, so players and enemies slip beneath the foliage (the occlusion
reveal keeps your own character visible under it). Forest ground
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

The shop has two tabs: **Wares** (the rolled stock) and **Buy Back** — the up
to ten most recent items sold this session wait on the merchant's counter and
can be bought back at exactly the price they fetched. The buy-back list is
session-scoped and clears when you leave.

### Passive skill tree

`P` opens a PoE-style passive tree — click-drag anywhere on the panel to PAN
around it (a press that doesn't move still allocates). One point per character
level past the first;
nodes allocate outward from the start node ("Adventurer's Spark"), each adding
stats through the SAME StatCollection pipeline as item modifiers — any existing
stat works as a perk with zero extra code. Allocation is server-validated
(existence, adjacency, unspent points) and persists in the character save. The
starter cluster is deliberately tiny (19 perks in three branches: melee, defense,
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

### Attributes & the three defense families

**Strength, Dexterity and Intelligence** are ordinary stats in the pipeline —
they come from gear rolls (of the Bear / Fox / Owl suffixes), passive nodes and
any future effect, and every derived conversion lives in ONE place
(`Stats/Defense.cs` → `AttributeBalance`; all numbers are placeholders until the
balance pass):
- **STR** → maximum Life + % physical damage.
- **DEX** → Deflection Rating + a little movement speed.
- **INT** → maximum Mana + % Energy Shield.

Three defense families hang off them:
- **Armor (STR)** — the existing reliable physical mitigation
  (`armor / (armor + 60)`), unchanged.
- **Deflection (DEX)** — every equipped piece contributes to ONE aggregated
  character **Deflection Rating** (pieces are never rolled separately), converted
  by a central level-scaled formula into a capped INITIAL chance. Each incoming
  direct **Attack** hit — regardless of its damage composition — then runs
  descending INDEPENDENT checks (initial, −15%, −30%, ... while above zero); a
  failed check never stops later ones, and every success deflects **20% of the
  remaining damage** (multiplicative: two successes on 100 → 64). Spells, DoTs,
  ground effects and slams are never deflected — the distinction is data-driven
  (enemy defs can flag projectiles as spells). Gear scales the RATING, not the
  per-layer strength.
- **Energy Shield (INT)** — a separate pool absorbed before Life, replicated in
  multiplayer alongside health. After `EnergyShieldBalance.RechargeDelay` seconds
  without taking damage (ANY damage resets the timer, even fully absorbed), it
  recharges at a %-of-maximum rate. A cyan bar caps the health orb when a build
  has any.

Armor comes in SETS per defense identity, two tiers each, every piece carrying
ONLY its type's stat: **Ironbound → Warplate** (Armor, STR), **Hide → Hunter's**
(Deflection, DEX) and **Cloth → Silk** (Energy Shield, INT) across
helmet/body/gloves/boots, plus three hybrid bodies — Brigandine (Armor+Deflection),
Battlemage Plate (Armor+ES), Shadowweave Garb (Deflection+ES) — that pay for
covering two defenses by carrying **~60% of each pure stat**. Gear demands
attributes as a BASELINE: maces and shields want Strength, staffs Intelligence,
Armor pieces Strength, Deflection pieces Dexterity, ES pieces Intelligence
(tier 1 ≈ 8, tier 2 ≈ 14), alongside character level. Requirements are enforced
server-side (attributes from OTHER equipped gear count) and shown in tooltips —
and the **of Ease** suffix locally reduces ITS item's attribute requirements by a
rolled percent (capped 60%), displayed pre-reduced and marked "(reduced)".
Tooltips also show pre-summed Deflection/ES totals and a short explainer of how
Deflection's descending checks work. The character sheet lists attributes, Armor
with its physical reduction, Deflection Rating with the initial chance, and
Energy Shield.

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

### Balance & scaling knobs

All tuning lives in `ARPG/Stats/Defense.cs` (attribute rates, Deflection and
Armor curves, XP penalties, enemy level scaling, Energy Shield recharge) or in
data JSON — see **BALANCE_CHANGES.md** in the repo root for the current pass's
before/after list. Highlights: kills more than 2 levels below you pay -25% XP
per level (floor 10%); any spawner/pack can set `EnemyLevel` to reuse an enemy
type at a higher level (+16% health / +14% damage / +22% XP per level above its
native def); Deflection and Armor both use level-scaled soft caps so defenses
decay unless gear keeps up; modifier tiers unlock across item level 1-100
(tier N at (N-1)*10). Summons pathfind via flow fields (the owner's live field
when following, a one-shot rally field when stationed), enemies aggro summons
with the same rules as players, and summon skills carry Melee/Projectile tags
so Skill Scrolls attach and ride their attacks.

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
  (507 checks, exit code 0 on success). It exercises `127.0.0.1`; LAN/ZeroTier use the
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
