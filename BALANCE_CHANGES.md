# Balance Changes — pre-generation pass

Every number here is data or a constant in `ARPG/Stats/Defense.cs` — nothing is
buried in gameplay code. This file is the review list for the pass that preceded
the generation/room-loop work.

## XP & leveling

| Change | Before | After |
|---|---|---|
| Gravebound Grunt XP | 18 | **11** |
| Grave Spitter XP | 26 | **16** |
| Barrow Knight XP | 34 | **20** |
| The Gravelord XP | 260 | **150** |
| Under-level kill penalty | none | **−25% XP per level** the enemy is more than **2 levels** below you, floor **10%** (`XpBalance`) |

Example: a level 13 character killing a level 8 grunt gets 25% XP; a level 40
character farming level-1 grunts gets the 10% floor.

## Enemy level scaling (new system)

`SpawnEnemy(..., level: N)`, plus `EnemyLevel` on every spawner/pack, spawns any
enemy type above its native level. Per level above the def (`EnemyLevelScaling`):

| Stat | Per level above native |
|---|---|
| Max health | **+16%** |
| Damage | **+14%** |
| XP reward | **+22%** |

A level-11 "zombie" (native 1) has 2.6× health, 2.4× damage, 3.2× XP. This is
the knob for reusing early enemies in late-game stages — set one number on the
zone/spawner.

## Enemy damage (up ~40%)

| Enemy | Before | After |
|---|---|---|
| Grunt | 4 Blunt + 3 Acid | **6 Blunt + 4 Acid** |
| Spitter | 8 Acid | **11 Acid** |
| Barrow Knight | 8 Slash + 2 Dark | **11 Slash + 3 Dark** |
| Gravelord | 9 Blunt + 5 Dark (slam 16) | **13 Blunt + 7 Dark (slam 22)** |

## Deflection & Armor nerfs

| Knob | Before | After |
|---|---|---|
| Deflection chance formula | `rating / (rating + 20 + 8·level)` | `rating / (rating + **30 + 14·level**)` |
| Armor reduction formula | `armor / (armor + 60)` (flat) | `armor / (armor + **40 + 10·level**)` (level-scaled, `ArmorBalance`) |

Both defenses now decay with level unless gear keeps up — a fresh character's
20 rating is 29% initial chance at level 1 and ~3% at level 40.

Gear values (roughly −30% across the board):

| Piece | Before → After |
|---|---|
| Leather Hood / Padded Vest / Worn Gloves / Sturdy Boots / Rope Belt (Armor) | 15/30/10/10/5 → **10/20/7/7/4** |
| Iron set (Armor) | 20/38/14/14 → **14/26/10/10** |
| Warplate set (Armor) | 32/55/22/22 → **22/38/15/15** |
| Hide set (Deflection) | 35/62/24/24 → **24/44/17/17** |
| Hunter's set (Deflection) | 52/90/38/38 → **36/62/26/26** |
| Cloth / Silk sets (Energy Shield) | unchanged (10/19/7/7 and 16/28/12/12) |
| Hybrid bodies (~60% of pure tier-2) | Brigandine **23 Armor + 37 Defl** · Battlemage **23 Armor + 17 ES** · Shadowweave **37 Defl + 17 ES** |

DEX still grants 2 rating per point — the harsher chance formula is the nerf.

## Item level 1–100 stretch

Modifier tiers (both prefixes.json and suffixes.json, every family):

| Tier | Old min ilvl | New min ilvl |
|---|---|---|
| 1 | 1 | 1 |
| 2 | 1 | **10** |
| 3 | 4 | **20** |
| 4 | 8 | **30** |
| 5 | 12 | **40** |
| 6 | 16 | **50** |
| 7 | 20 | **60** |
| 8 | 24 | **70** |
| 9 | 28 | **80** |
| 10 | 32 | **90** |

Gear required levels:

| Gear | Old | New |
|---|---|---|
| Starter armor (Leather/Padded/etc.) | 1 | 1 |
| Tier-1 sets (Iron / Hide / Cloth) | 2 | **5** |
| Hybrid bodies | 2 | **20** |
| Tier-2 sets (Warplate / Hunter's / Silk) | 2–6 | **30** |
| Wooden Club / Oak Staff / Buckler | 1 | 1 |
| Iron Mace / Mystic Staff / Kite Shield | 4–5 | **15** |
| Heavy War Mace / Arcane Staff / Tower Shield | 8–9 | **35** |

(There is deliberately no gear past level 35 yet — new tiers slot into the
40–100 band as zones for them exist. Attribute requirements unchanged: ~8 tier
1, ~14 tier 2.) The F1 "give weapon/shield" debug commands now hand out gear
the character can actually equip.

## Fixes riding this pass (not number changes)

- **Summon pathfinding**: summons route via flow fields — the owner's live
  field when following, a one-shot BFS field from the rally point when rallied —
  instead of naive point-chasing that wedged on walls.
- **Aggro symmetry**: enemies aggro, chase and attack SUMMONS with the same
  rules as players (nearest threat wins, same leash) — a pack fighting far from
  its owner gets fought back, and summons no longer tank without retaliation
  bugs when commanded outside the player's own aggro bubble.
- **Summon scrolls**: Skeleton Warriors carry the `Melee` tag, Archers the
  `Projectile` tag — melee/projectile Skill Scrolls attach and their effects
  ride the summons' attacks (attack speed via the skill's cooldown ratio, extra
  Multishot arrows, all on-hit ailment chances on both arrows and sword hits).

## Suggested next balance targets (not changed yet)

1. **Life regeneration**: flat regen rolls don't scale with level, but they're
   what makes melee unkillable early — consider %-of-max regen values or a
   "recent damage" gate like ES has.
2. **Knockback stunlock**: repeated knockback keeps melee enemies out of swing
   range permanently; diminishing knockback per recent hit would let the enemy
   damage buff actually land.
3. **Crit**: chance is capped (75) but crit damage stacks without limit —
   fine now, watch it when tier 6+ suffixes come into play.
4. **Boss slam** has no telegraph decal — with enemy damage up it may deserve
   the wind-up treatment melee got.
5. **Shop prices** don't scale with character level — gold inflates by mid-game.
6. **ES recharge in combat**: 4s delay is generous while kiting; consider 5-6s
   once the room loop makes disengaging harder.


---

# Addendum — second tuning pass

## Summon mana: cost → RESERVATION (behavior change)

Summons no longer pay a one-time mana cost. Each minion **reserves**
`flat + 5% of maximum mana` while it exists: your usable pool shrinks (dimmed
band on the orb, "N reserved" label), regen only refills the unreserved part,
free respawns keep the hold, and dismissing releases it. Raising a minion needs
enough UNRESERVED maximum mana, not current mana. This matches the original
design intent ("each summon should take ... of your maximum mana").

## Level scaling (stats matter more)

| Knob | Before | After |
|---|---|---|
| Health per character level | 8 | **4** |
| Mana per character level | 4 | **2** |

## Spell tuning

| Skill | Before | After |
|---|---|---|
| Fire Bolt mana | 5 | **7** |
| Chain Lightning mana | 8 | **11** |
| Ice Spike base damage / per level | 15 / 4.5 | **12 / 4** |
| Arcane Burst mana | 9 | **16** |
| Arcane Burst cast | instant | **0.45s charge-up** (wind-up like Mace Slam) with a new gathering-energy telegraph and a reworked two-ring detonation visual |

## Dexterity → Deflection (no more free defense)

DEX no longer grants flat rating (was 2/point — 20 free rating with zero gear).
It now **multiplies gear rating by +0.4% per point**: no deflection gear means
no deflection, and bare attribute points only amplify what armor provides.

## Armor curve + values (early %-reduction reined in)

| Knob | Before | After |
|---|---|---|
| Soft cap | `armor + 40 + 10·level` | `armor + **60 + 12·level**` |
| Starter set Armor | 10/20/7/7/4 | **8/15/5/5/3** |
| Iron set | 14/26/10/10 | **10/19/7/7** |
| Warplate set | 22/38/15/15 | **16/28/11/11** |
| Hybrid bodies' Armor | 23 | **17** |

A full starter set (~33 armor) is now ~31% reduction at level 1 and falls off
fast without upgrades (was ~45%+ under the old base-40 denominator).


---

# Addendum — third pass (attributes, drops, slams)

## Attributes carry more weight

| Knob | Before | After |
|---|---|---|
| Life per Strength | 0.5 | **2** |
| Mana per Intelligence | 0.5 | **2** |

Dexterity unchanged. With half-rate per-level growth (previous pass), attribute
points are now the main way health and mana pools grow.

## XP down again (~30%)

| Enemy | Before | After |
|---|---|---|
| Gravebound Grunt | 11 | **8** |
| Grave Spitter | 16 | **11** |
| Barrow Knight | 20 | **14** |
| The Gravelord | 150 | **100** |

## Drops: parity, availability & level scaling

- **Weapon weights trimmed**: Mace/Staff category weights 120 → **80** (both
  loot tables) — armor slots no longer lose most rolls to weapons.
- **Level-1 Deflection set** (Poacher's: hood 18 / tunic 33 / gloves 13 /
  boots 13 rating, 8 Dex) and **level-1 Energy Shield set** (Novice's: cowl 8 /
  robe 15 / wraps 5 / slippers 5 ES, 8 Int) — every defense flavor now drops
  from the first kill, at the same starter weight (100) as the Armor set.
- **Hybrid bodies drop from level 5** (were 20).
- **FIX — drops now roll at the enemy's SCALED level**: a level-11 zombie from
  a leveled spawner previously dropped level-1 loot (the def's native level was
  passed to the loot roll); it now drops level-11 loot, and gold scales off the
  same level.

## Boss slam: telegraphed + bigger

| Knob | Before | After |
|---|---|---|
| Slam radius | 2.4 | **3.2** |
| Telegraph | none (instant) | **0.9s red AoE decal** (`SlamWindup`, per enemy) |

The Gravelord now commits: a pulsing red MMO-style circle marks the full slam
area during the wind-up, damage resolves against where everyone stands at the
END of the wind-up — walking out of the circle dodges it entirely. Stuns and
freezes cancel the slam.

## Ground Slam rework

| Knob | Before | After |
|---|---|---|
| Cast | instant | **0.4s wind-up** (overhead raise) |
| Mana | 8 | **12** |
| Cooldown | 0.8s | **2.2s** |
| Knockback | none | **1.8 tiles** |
| Visual | plain ring | **cracked-earth fissures + debris/dust storm around the caster** |

## Party XP share

| Role | Share of a kill's XP |
|---|---|
| The player who landed the kill | **100%** |
| Every other player in the game | **70%** (`XpBalance.PartyShare`) |

Each member's own under-level penalty applies to their own share, and skill XP
still follows only the skill that landed the blow — but a high-damage or
fast-attacking build no longer starves the rest of the group of character XP.

## Starter economy (campaign)

| Knob | Value |
|---|---|
| Starting gold | **100** |
| Starting skills | **Mace Strike + Fire Bolt** only |
| Skill price at the trainer | **75 gold** each |
| Chest gear | 2 plain level-1 items per hub chest (4 chests, once per session) |
| Campaign enemy level | **1 + 3 × (loop − 1)** — loop 2 = 4, loop 3 = 7 |
| Gravelord adds | 3 spitters at HIS level, first at ~11s engaged, every 26s after |

## Mana economy (batch 18)

| Knob | Value |
|---|---|
| Mana regen from Intelligence | **+1% per point** (`AttributeBalance.ManaRegenPctPerIntelligence`) |
| Mana cost per skill level past 1 | **+10%** (`SkillMath.ManaCostPerSkillLevelPct`) |
| Mana cost per attached Skill Scroll | **+20%** (`SkillMath.ManaCostPerScrollPct`) |

A level-3 Multishot Fire Bolt costs 7 × 1.2 × 1.2 ≈ **10 mana** instead of 7 —
power drinks deeper. The K menu shows the real cost.

## Potion flasks (new — superseded by the batch-19 item rework below)

| Knob | Value (`PotionBalance`) |
|---|---|
| Flasks | Health (`Q`) + Mana (`E`), always carried, never instant |
| Restore | **40% of max over 4s** (mana respects summon reservations) |
| Charges | **3 max**, start full, **+1 per kill** (every player, both flasks) |

## Loop-2 Gravelord: dash charge (new)

| Knob | Value (gravelord def) |
|---|---|
| Unlock | enemy level **7+** (`DashMinLevel`) — the loop-2+ campaign boss |
| Prepare | **1.0s** rooted, with a red MMO ground LINE telegraph (direction locks at start) |
| Charge | **7 tiles at speed 13**, one hit per player: **34 Blunt** contact damage + shove aside |
| Cooldown | **9s**; stuns/freezes cancel both prepare and charge |

## More attribute passives

Six new tree nodes: Titan Blood / Colossus (**+10/+8 STR**), Viper Reflex /
Ghost Step (**+10/+8 DEX**), Starlit Mind / Sage (**+10/+8 INT**) — each branch
now carries four attribute nodes.

## Mace Strike reach fix (behavior)

Plain swings now hit **player-centered**: enemies within the skill's Range of
the CASTER (plus their body radius) inside a ~140° arc toward the aim, with
point-blank enemies always caught. The old test (a circle around the projected
impact point) could reach range+radius ahead while whiffing an adjacent
off-axis enemy — the "hits far, misses close" report. Aimed area slams (Mace
Slam) keep the placed-circle behavior by design.

# Addendum — batch 19 (flask items, summons, loot window)

## Flasks are equippable ITEMS (rework)

Flasks moved off `PotionBalance` percentages onto item bases with real stats:

| Base | Lvl | Restores | Charges | Duration |
|---|---|---|---|---|
| Minor Health Flask | 1 | **55 life** | 3 | 4s |
| Minor Mana Flask | 1 | **45 mana** | 3 | 4s |
| Greater Health Flask | 14 | **120 life** | 3 | 4s |
| Greater Mana Flask | 14 | **95 mana** | 3 | 4s |

- Two dedicated equipment slots (`Flask1`/`Flask2`, either order); new
  characters start with the Minor pair equipped; pre-flask saves are migrated.
- Flasks drop as loot (category weight 30, always Normal rarity, always full).
- **Charges never regenerate** — the kill refill is gone. The Sanctum
  **fountain** (mid-room, `F` beside the basin) refills every carried flask,
  and every flask starts a play session full.
- Restore stays over-time; mana still respects summon reservations.

## Spell base costs trimmed

Costs now scale +10%/skill level (+20%/scroll), so the bases came down:
Fire Bolt **7 → 6**, Ice Spike **6 → 5**, Chain Lightning **11 → 9**.

## Summons: +10 base HP, live reservation reprice, rally-block fix

- Skeleton Archers **30 → 40** base HP, Skeleton Warriors **48 → 58** — paying
  the pack back for the tighter mana economy.
- **Bug fix**: leveling a summon skill now reprices the reservation of minions
  ALREADY out (and those awaiting a free respawn) — previously only future
  summons paid the higher price.
- **Bug fix**: a summon marching to a rally point set BEHIND enemies used to
  shove against the bodies forever without swinging. Marching summons now
  fight any enemy within **2.2 tiles** (a physical blocker), then resume the
  march when it dies; distant enemies still don't distract the march.

## Loot: base-level window

Drops at item level N only roll bases with `RequiredLevel ≥ N − 25`
(`LootGenerator.BaseLevelWindow`) — late zones stop dropping leather hoods.
If the window would empty the pool (ilvl far above current data), generation
falls back to the full pool rather than dropping nothing.

## Generation: orphan-stair cleanup

Later passes (boss arena, ponds, spawn pocket, corridor fallback) could
flatten a terrace and leave its staircase embedded in flat ground, climbing
to nothing. `CleanOrphanRamps` now removes any ramp whose ascent side doesn't
reach walkable ground one level up (or whose low side isn't walkable at its
own level) before the connectivity pass runs; tests assert zero orphan ramps
across six seeds.

# Addendum — batch 20 (buy-back, DoT stacks, XP/freeze tuning)

## Merchant buy-back (new)

The shop grew a second tab: everything sold this session (up to the **10**
most recent, `BuybackSlots`) waits on the merchant's counter and can be
bought back **at exactly the price it fetched**. The list rides the ShopStock
packet and clears when the session ends.

## Bleed & poison STACK (rework)

One enemy used to hold at most one bleed and one poison total. Now every
instance is a stack with its own tick rate and 4s timer:

| Rule | Value |
|---|---|
| Stacks per SOURCE (one player's one skill) | **1** base |
| Rending / Venom scroll on that skill | **+1 max stack each** |
| Different skills / different players | always coexist, no shared cap |
| A full source rolling a NEW instance | stronger → replaces its weakest stack; weaker → only refreshes it |

The strongest instances always survive — a 4-damage-a-tick bleed is never
overwritten by a weaker follow-up. Total DoT is the sum of every live stack.

## Enemy XP down again (~20%)

Grunt **8 → 6**, Spitter **11 → 9**, Barrow Knight **14 → 11**,
Gravelord **100 → 80** (per-level scaling curves unchanged).

## Freeze + chill readout

- Deep freeze (chill at 100%) base duration **1.4s → 2.2s** (bosses still
  thaw at 40% of that).
- The chill debuff icon over an enemy now shows the buildup: a fill strip
  along the icon's foot plus the raw percent above it — 100% is the freeze
  threshold. The value rides the enemy state snapshots (0–100).

## UI fixes

- The inventory's gold readout moved below the drag bar (it clipped over the
  grip dots).
- A right-click that quick-equips an item in the bag no longer doubles as a
  cast for skills bound to RMB — mouse-bound hotbar slots now respect UI
  capture, exactly like the left button.

# Addendum — batch 22 (loot on terrain, stair quality, sanctum stonework)

## Drops rest ON the terrain (bug fix)

Loot scatter used to keep the dying enemy's height while jittering onto any
non-wall tile — at a terrace edge an item could land on lower/higher ground
with the wrong stored height, failing the pickup height gate forever ("fell
through the cliff"). Scatter now only accepts spots standable at the source's
elevation, and every drop's stored height snaps to the surface at its landing
spot (bridge decks keep their deck height).

## Connect-pass stairs look intentional (rework)

The reachability pass used to carve a single stair at whatever boundary tile
the scan touched first — lone steps poking out of plateau interiors, some
climbing out of ponds. It now scores every possible spot (a clean level
walk-up strictly dominates; mid-cliff placement preferred), carves proper
**two-wide flights** where the cliff allows, and when a stranded pocket's only
viable cliff is hugged by water or a pillar it opens that one approach tile (a
stone ford) instead of leaving a stair rising out of water. Across 400 test
seeds: awkward lone stairs down **316 → 5**; the fixed test seeds assert zero.

## The Sanctum's purple stonework (new tileset)

A new `sanctum` zone theme with `StoneBrick` masonry rendering: floors draw as
laid purple stone slabs (baked mortar seams + per-slab tone), walls as
brick-coursed purple faces (running bond, per-brick tone variation). The hub
always uses it — run maps keep the campaign zone theme; clients swap at the
door via the map packets' theme id.

# Addendum — batch 23 (stash, death rules, bows, new undead)

## Stash containers (new)

Storage is tied to physical CONTAINER objects, not one global array: the
character carries `Stashes[containerId]` grids (10x8), and the hub's stash
chest is the first container (`hub_stash`) — future player rooms add more ids.
Open it with `F` beside the chest (the bag opens alongside); every move is a
server-validated `MoveItem` with a reach check against the container, and
contents persist on the character save.

## Death & revival (new rules, campaign)

- Dying in the campaign leaves a corpse — **no auto-respawn**.
- A living teammate revives you by standing beside the corpse and **holding
  the interact key for 2.5s** (server-timed channel with a progress bar; an
  interrupted channel bleeds back down). Revived at **50% health**, in place.
- **Full party wipe = the run ends**: after a short beat the whole group is
  returned to the Sanctum, alive at full health. Walking home through the hub
  door also stands any dead teammates back up.
- Arena mode keeps the old quick respawn (tests and debugging).

## Bows & quivers (new weapon class)

| Base | Lvl | Damage | APS | Range |
|---|---|---|---|---|
| Short Bow | 1 | 3-6 | 1.5 | 11 |
| Hunting Bow | 15 | 7-12 | 1.4 | 11.5 |
| War Bow | 35 | 13-22 | 1.25 | 12 |

Bows are **two-handed but share with a quiver**: quivers equip into the
off-hand (only alongside a bow; anything else follows the normal two-handed
rules) and carry an implicit **+6/9/12% attack speed** by tier
(Leather/Fletcher's/War Quiver, levels 1/15/35). Bow attacks deal **Thrust**.
**Arrow Shot** (trainer, 75g): a **zero-mana** weapon-damage arrow, 1.1x
weapon damage +8%/level. Both categories drop as loot.

## New undead

- **Crypt Leaper** (loop 1+): a small fast ghoul that LEAPS — the boss-style
  ground-line telegraph (0.65s), then a 5.5-tile lunge for 12 contact damage
  every 5s. XP 7.
- **Grave Caller** (loop 2+): a robed conjurer that raises **2 Shamblers**
  every 13s (first at 3.5s) and drops a purple-telegraphed **dark AoE**
  (16 Dark, radius 1.9, range 7.5, 1.1s windup — the circle locks at cast
  start, walk out to dodge). XP 14.
- **Shambler**: the Caller's fodder — a runty zombie (12 hp, 4 damage, XP 2,
  renders at 72% size).

---

# Addendum (batch 24): classes, body sprites & the bow affix fix

## Starting classes (kits, not restrictions)

Character creation now picks one of three classes from
`Data/Classes/classes.json`. A class is ONLY a starting kit — items, skills
and progression never check it again:

| Class | Gear | Skill (hotbar slot 1) |
|---|---|---|
| Warrior | Wooden Club | Mace Strike |
| Archer | Short Bow + Leather Quiver | Arrow Shot |
| Mage | Oak Staff | Fire Bolt |

Every kit still includes the Minor Health/Mana flask pair and 100 gold. The
old default (club + staff in the bag, two skills known) is gone — fresh
characters know exactly ONE skill, which nudges the 75g trainer economy
earlier.

## Body sprites & appearance

One shared 16x27 human rig (idle + two stride frames), two body styles
(male/female silhouettes), six skin tones. Body style + tone are chosen at
creation, saved on the character, and replicate through `PlayerAppearance`
(protocol **v27**) so every client renders every player's body. State tints
(death grey, freeze blue, dodge flash) now apply to the body sprite.

## Bows & quivers could not roll affixes (bug fix)

Every weapon modifier listed explicit categories that predated bows, so
magic/rare bows and quivers generated with ZERO modifiers — blank uniques-
without-the-unique. Now:

- **Bows** roll everything maces roll: added damage (all 7 elements), phys%,
  attack speed, crit chance/damage, health/mana, mana regen, modifier limit,
  reduced requirements.
- **Quivers** roll the offensive subset: added damage, phys%, attack speed,
  crit chance/damage.

A regression check in the test suite counts the bow/quiver affix pools so
this can't silently regress (suite is now **495 checks**).

---

# Addendum (batch 25): hair styles & free appearance colors

- **Hair styles**: Short, Long, Bun, Bald — chosen at creation, independent of
  body style (either body wears any hair).
- **Free colors**: skin AND hair are now full 24-bit RGB. The creation screen
  keeps six quick-pick swatches for each, but R/G/B sliders reach every color;
  the live preview re-bakes the real sprite as the sliders move.
- **Protocol v28**: `PlayerAppearance` carries body style, hair style and both
  exact RGB values, so custom colors replicate byte-exact to every client.
  Saves from before this patch keep working — their preset tone index becomes
  the fallback color, and hair defaults to the old per-body look (male short,
  female long).
- Test suite stays at **495 checks** (appearance replication now asserts a
  custom non-preset color round-trips; the two-handed-staff bag-swap test was
  made deterministic — it could rarely fail on random bag pressure).

---

# Addendum (batch 26): worn armor draws on your character

Armor is no longer invisible: what you wear now paints onto the body sprite,
replicated to every player (protocol **v29**).

- **Body armor** silhouettes: cloth (full robe over the legs), leather
  (stitched jerkin), mail (ring texture), plate (pauldrons + chest ridge).
- **Helmets**: hood, cowl, cap (open face), helm (full faceplate with eye
  slit). Any helmet hides hair.
- **Gloves / boots / belt** recolor their rig pixels — cheap but visible.
- Every armor base in `armor.json` now declares its overlay style and
  garment color; sets keep a family palette (iron grey, warplate dark steel,
  hide tan, hunter green, silk white...).
- All layers paint over the same rig coordinates, so every piece fits both
  bodies — and the same contract holds for future pre-rendered art.
- Suite is now **499 checks** (armor visual data completeness, the
  `equip_set` dev command, and five-slot appearance replication).

---

# Addendum (batch 27): your character turns with your aim

4-way directional facing, driven by the mouse: the body sprite now shows a
front, back, or true side profile (mirrored for west) picked from the aim
direction. Hair, helmets and armor are all drawn per-view — a full helm's
eye slit only exists on the front, hoods close up from behind, long hair
falls down the back. Walking stays the only body animation by design;
weapons keep hovering toward the cursor. The creation screen preview now
turns on a slow turntable so you see all four sides before committing.
Suite: **500 checks**.

---

# Addendum (batch 28): readability pass — swing layering, armor icons, the bow

- **Up-swings layer behind the body.** A melee swing aimed away from the
  camera used to draw the weapon on top of the character's head; it now
  arcs behind the sprite like the held weapon already did.
- **Armor shows as icons in the bag.** Every worn-armor category renders a
  shaped, tinted glyph instead of text initials — hoods/cowls/caps/helms,
  robes/jerkins/mail/cuirasses, gloves, boots, belts, plus amulets and
  rings. Shapes follow the piece's ArmorStyle and colors its SpriteColor,
  so an iron cap and a silk cowl read apart instantly.
- **The bow is a bow now.** New held sprite: an upright stave with the
  string on the chord and a leather grip — no baked-in arrow (arrows only
  exist as flying projectiles). It hangs vertically at the side like a
  carried bow.
- **Quivers confirmed live**: they were always in the drop pool and the
  merchant's roll (and the archer kit starts with one) — just uncommon at
  weight 45. A new deterministic test pins the pool wiring (~2% of drops).
  Dev aid: equip_set gained an "archer" family (bow + quiver + hunter set).
- Suite: **501 checks**.

---

# Addendum (batch 29): the bow foreshortens with your facing

Facing left or right you see the bow's full arc and string; facing toward or
away from the camera the bow's plane is perpendicular to the screen, so it
now draws EDGE-ON — a slim stave with the grip wrap and flared tips, no arc.
Pure rendering; the bag icon keeps the full-arc silhouette for readability.

---

# Addendum (batch 30): weapon damage math cleanup + bow polish

- **Added-damage prefixes now ride EVERY weapon attack.** They were gated to
  Melee-tagged skills, so a bow's fire/cold/etc. rolls buffed your punches
  and did nothing for Arrow Shot. The gate is gone: added attack damage
  applies to all weapon-driven attacks, arrows included.
- **Weapon-local damage totals (PoE-style).** A weapon's own flat added
  phys AND its own %Physical rolls now fold directly into ITS damage:
  (base + flat) x local% — and the tooltip shows exactly that total, the
  way armor pieces already total their defenses. The weapon's %phys is
  removed from the global pool so it never double-dips; %phys from rings,
  amulets, passives and Strength stays global and multiplies the whole
  number (added damage included). Quiver flat-phys rolls now ride the
  bow's damage (they previously did nothing).
- **The bow mirrors when facing west** — the arc opens toward the aim on
  both sides instead of drawing backwards on the left.
- Suite: **504 checks** (ranged added-damage, local-vs-global %phys,
  quiver flat-phys wiring).

---

# Addendum (batch 31): elemental adds display on attack weapons

Elemental added-damage rolls (fire / cold / lightning / acid / dark / light /
arcane) on maces, bows and quivers now show as COMPUTED damage lines at the
top of the tooltip — "Fire Damage: 4-6", colored by type, using the same
0.8x-1.2x spread the combat math rolls. Combined with batch 30, the design
is now one clean rule: a single generic "adds X damage" affix family works
identically on every attack weapon, melee or ranged — no melee-only or
ranged-only variants needed. Staffs are untouched: their spell-damage adds
are separate stats that scale spells, and they stay in the modifier list.
The character sheet's per-type DPS breakdown already included these
components, so tooltip, sheet and hits all agree.

---

# Addendum (batch 33): the character roster

No more one-save-per-install: starting any game now opens a CHARACTER SELECT
screen listing every save on disk — live sprite preview (body + worn armor),
name, level, class, gold — pick to play, or delete with a two-stage confirm.
Creation gained a NAME field (unique per save, auto-suggests a free name),
so alts are first-class. The chosen character is your multiplayer identity;
the name inputs on the Host/Join screens are gone.

---

# Addendum (batch 34): class attributes + the lighting pass

## Class base attributes

| Class | STR | DEX | INT |
|---|---|---|---|
| Warrior | 11 | 4 | 3 |
| Archer | 3 | 12 | 4 |
| Mage | 4 | 2 | 12 |

Base attributes now come from your CLASS instead of a flat 10/10/10 — which
makes early gear requirements (the tier-1 8s) actually gate: warriors wear
leather and iron but not silk, mages wear cloth but can't lift mail, archers
live in hide. Gear and passives stack on top as before; saves from before
this patch keep flat 10s. HP/mana/phys%/deflection all flow from the new
numbers (a fresh warrior fields ~82hp to the mage's ~68).

## Alpha-blend lighting

Dark zones (sanctum, graveyard, tomb) now multiply the scene by an ambient
light level, with additive radial lights cutting through: player torchglow,
projectile streaks, NPC/fountain/door glows, impact flashes. Daylight zones
skip the pass entirely; Options -> Gameplay can toggle it. Suite: 510 checks.

---

# Addendum (batch 35): light as a stat, elemental spell lights, burning ground

- **Light radius is a character stat.** Base 235px torchglow, grown by the
  new "of Radiance" suffix family (10 tiers, +12% to +72%, rolls on BODY
  ARMOR and HELMETS only) — replicated, so your party sees your bigger
  glow. NPCs no longer emit their own light; the merchants wait in the dusk.
- **Spell lights follow the element.** Projectiles only glow when their
  damage type does: fire bolts burn warm orange, lightning crackles
  blue-white; arrows and other physical projectiles fly dark. Chain
  lightning lights every strike point along the chain; electrocute's zap
  arcs flash too.
- **Scorched Earth ground fire reworked:** charred ground that blackens as
  it burns, a pulsing coal bed, two-tone licking flames, rising embers,
  drifting smoke — and its own flickering firelight.
- Added-damage modifier text corrected to "to Attacks" (they stopped being
  melee-only in batch 30). Suite: **512 checks**. Protocol v30.

---

# Addendum (batch 36): continuous gear requirement validation

- **The attribute-bootstrap exploit is dead.** Previously requirements were
  checked only at equip time: wear a +INT amulet, equip an INT robe, remove
  the amulet — the robe kept working. Now `StatCalculator` re-validates every
  equipped piece on every recompute: an item only counts while its level and
  attribute requirements are met by everything EXCEPT itself (base attributes
  + passives + the OTHER active pieces). Deactivation cascades to a fixed
  point, so a chain of gear that only stood on a removed piece collapses
  with it.
- **Inactive gear = not worn.** It stays in the slot but grants no stats, no
  Armor/ES/Deflection, no weapon damage (an inactive weapon swings as
  unarmed). Server and client share the same computation, so gameplay and UI
  always agree.
- **Red-letter warning.** The inventory paints an unmet piece with a red
  tint, border and `!` badge; its tooltip leads with **REQUIREMENTS NOT
  MET — This item grants no benefits.** and the Requires line turns red.
- Suite: **516 checks** (bootstrap + cascade regressions; ES/deflection test
  puppets now genuinely qualify for their gear).

---

# Addendum (batch 37): rarity pacing + skill scroll sprites

- **Gold (rare) drops are now the jackpot, not the routine.** Default loot
  table rarity weights went 45/38/17 (Normal/Magic/Rare) -> **45/47/8**:
  rares cut roughly in half, blues absorbing the difference so total drop
  volume is unchanged. Boss kills lean the same way: 30/70 Magic/Rare ->
  **55/45** — still far richer than trash, but a gold is no longer near-
  guaranteed.
- **Skill Scrolls have real sprites.** An UNROLLED hanging scroll (wooden
  dowel, dangling parchment, curled foot) — a deliberately different
  silhouette from the rolled-horizontal Enchanting Scrolls — with a themed
  accent color and a rune glyph per scroll (fire orange bolt, poison green,
  bleed crimson cross, ice blue, arcane violet, ...). Drawn in the inventory
  / shop / stash grids, on the ground as world drops, and in the Skill Menu's
  attached-scroll slots (which used to show a bare initial).

---

# Addendum (batch 38): summoner kiting, AoE vs summons, dappled forest light

- **The Grave Caller is a coward now.** New data-driven `KeepDistance` (its
  comfort ring, 3 tiles): inside it the caster BACKS AWAY from the nearest
  threat instead of trading zombie swipes — the fight stays engaged (its
  dark AoE and shambler-raising keep running while it retreats), and the
  melee swipe only comes out when it is genuinely cornered against a wall
  (retreat and sidestep both blocked).
- **Boss AoEs respect summons as threats.** The Gravelord's ground slam now
  triggers off the nearest engaged threat — player OR summon — and the
  shockwave damages and knocks back summons inside the ring. Same for
  telegraphed caster circles (Grave Caller): a summon pack is a legitimate
  cast target, and minions standing in the circle burn when it resolves.
- **Mirewood runs dappled daylight.** The forest theme now uses the lighting
  pass: a dim green ambient (B4BCAA) with seeded pools of warm sunlight
  (canopy gaps / passing clouds) scattered across walkable ground, each
  slowly breathing on its own phase. Same layout on every client from the
  map seed; toggleable via Options -> Gameplay -> Lighting like all zones.
- Suite: **523 checks** (kiting retreat, slam-on-summons trigger + damage,
  theme data).

---

# Addendum (batch 39): weather — rain / snow / wind with real shelter

- **Weather is a local toggle, not zone data.** Options -> Gameplay ->
  Weather cycles Off / Rain / Snow / Wind (default Off). Purely cosmetic and
  client-side — no network state, no theme coupling — so any zone can rain.
- **Particles live in WORLD space, so shelter is real.** New
  `GameMap.IsSheltered(pos, height)`: under a bridge deck (below its level —
  the deck itself stays exposed) and anywhere near a big tree's canopy, rain
  and snow never spawn. Height-aware: the same tile is dry underneath the
  bridge and wet standing on it. Future interiors plug their roofed tiles
  into the same query. Everything lands on the column's actual surface —
  deck, wall top, or ground.
- **Rain**: slanted storm-leaning drops, splash rings where they land, and
  an overcast pass — ambient dims ~14% in dark zones, and even daylight
  zones get a light overcast so the rain reads. **Snow**: drifting, swaying
  flakes that settle and melt over a second. **Wind**: tumbling leaves with
  faint gust streaks (wind ignores shelter — leaves blow through).
- Overcast hides the forest's sun patches while it rains or snows.
- Drawn inside the world pass, so zone lighting dims weather like everything
  else. `ARPG_DEVUI=weather:rain|snow|wind` forces it for screenshots.
- Suite: **528 checks** (bridge under/on shelter, canopy shade, open-ground
  exposure, toggle default + mode list).

---

# Addendum (batch 40): weather belongs to MAPS, not settings

- Clarified design intent: weather is an attribute maps CAN carry, never a
  player preference. The Options -> Gameplay weather cycler is gone;
  `GameSettings.Weather` no longer exists.
- `ZoneTheme.Weather` (optional, default none) flows into `GameMap.Weather`
  at generation — clients rebuild maps from the shared seed, so everyone
  sees the same sky for free. No shipped theme forces weather yet; authored
  maps and future areas/interiors can set or override it per region.
- The F1 debug menu gains a LOCAL test cycler: Map default -> Rain -> Snow
  -> Wind -> Off -> back to Map. Renderer-side override only — nothing is
  saved, nothing replicates. `ARPG_DEVUI=weather:<mode>` sets the same
  override for screenshots.
- Suite stays at **528 checks** (the settings-toggle check became a
  map-attribute check: theme default flows into the map, nothing forces it).

---

# Addendum (batch 41): corpses, blood, and a dirty weapon

- **The dead stay behind.** Kills now leave an authoritative SERVER corpse
  record (id, enemy type, position) replicated to every client — the
  foundation for skills that target the dead (raise dead, corpse explosion,
  devour). Capped at 120 oldest-first, cleared on map transitions, included
  in the join snapshot so latecomers see the same battlefield. Protocol v31.
- **Death looks like death.** Clients play an ease-out topple (pivot at the
  feet, side picked per corpse so packs don't stack identically), the body
  dims, and a pool of the victim's blood spreads beneath. Corpses sort
  under living entities, so the fight walks over the dead.
- **Blood is data-driven per enemy** (`Blood` on the enemy definition):
  dark red flesh by default, ACID GREEN for spitters, PALE BONE DUST for
  skeletons, dark ichor for the Gravelord. Physical hits (blunt/slash/
  thrust) burst 9 droplets under gravity plus a brief ground speckle —
  spell hits stay clean.
- **Your weapon remembers the kill.** A melee hit that draws blood stains
  the swinger's weapon with a splatter mask over its striking half, tinted
  with THAT victim's blood color, fading out over ~9 seconds (refreshed by
  every fleshy hit). Whack a spitter and your mace drips green.
- Suite: **532 checks** (server corpse records + full replication, blood
  color data, corpses cleared on transition).

## Batch 41 follow-up (playtest notes)

- **Skeletons no longer bleed at all**: empty Blood on the definition means
  no hit burst, no corpse pool, and nothing sticks to your weapon — a slain
  Barrow Knight leaves a clean pile of bones.
- **Weapon gore builds up gradually.** One hit leaves a few FLECKS, a couple
  more a real SPLATTER, and only sustained swinging soaks the striking end
  (three stages, each a superset of the last — droplets stay where they
  landed as more join). As the coat dries it thins back down through the
  same stages before vanishing, instead of popping off at full strength.

---

# Addendum (batch 42): gore & effects polish pass (playtest notes)

- **Blood pools are pixel splats now**, not neat ellipses: a scatter of
  irregular blood pixels appears a few at a time around a settling body,
  and hit-splatter ground speckles sort UNDER dropped items — blood never
  covers your loot.
- **Zombie blood is putrid green** (grunt/shambler/leaper each a slightly
  different rot; the Gravelord bleeds deep bog green). The Grave Caller — a
  living cultist — still bleeds red. Skeletons remain bloodless.
- **Dead bodies get their own sprites.** Corpses topple as before but now
  SETTLE into a dedicated dead-heap sprite per style: zombies/ghouls slump
  into a mound with a lolled head and outflung arm, skeletons scatter into
  a bone pile around the skull, necromancers collapse into a crumpled robe.
  No more tipped-over walking frames.
- **Fire Bolt is a fireball**: yellow-white core, orange body stretched
  along the flight, a flickering three-blob flame tail and embers popping
  off the wake — instead of a flat orange orb.
- **Shatter shards look like broken ice**: a compact faceted CHUNK (8x8)
  clearly distinct from the long tapered Ice Spike that burst.
- **Slam zones are readable**: both Mace Slam and the boss ground slam draw
  a crisp dashed RING at the exact damage radius (snaps out, holds, fades)
  over a softer fill, and debris got wilder — more dust and half again as
  many rock pixels kicked across a wider, more random spread than the
  damage circle itself.

## Batch 42 follow-up: corpse sprites, second pass

- The hand-drawn heap sprites read as abstract blobs, so corpses are now
  DERIVED from the enemy's own standing sprite: frame 0 rotated onto its
  side, squashed ~40% flat against the ground, darkened, with hash-ragged
  silhouette edges — the body stays recognizably THAT enemy (helmet, cape,
  colors and all), just unmistakably dead. Skeletons additionally shake a
  few loose bone pixels out of the pile.

---

# Addendum (batch 43): staining, smaller sprays, jitter fix, living light

- **Weapon gore reads as a STAIN now**: the splatter mask is sparser and
  darker (soak marks, not paint), capped at ~55% opacity — blood soaks INTO
  the weapon head instead of coating it. Build-up stages unchanged.
- **Per-hit blood is smaller**: every strike sprays, so each spray is now a
  handful of 1-2px droplets in a tight arc with a 3-pixel landing speckle —
  gore accumulates across a fight instead of arriving all at once.
- **Fixed the map-wide ±1px shimmer while walking.** The camera followed the
  player at sub-pixel precision, so every rock/mushroom/grass tuft crossed
  its own integer-rounding boundary on a different frame. The camera's
  iso-space offset now snaps to whole pixels: the world scrolls in lockstep
  and static scenery is rock solid (ScreenToWorld mirrors the snap so aim
  stays exact).
- **Forest sun pools live**: each pool slowly swells and shrinks (~100s
  cycle), creeps sideways a few pixels like the sun's angle drifting, and
  shimmers on two overlapping periods — no more single frozen shape for the
  lifetime of the map.

---

# Addendum (batch 44): stun buildup, status bars, compounding XP curve

- **Stun is a BUILDUP now, not a coin flip.** Hits with a StunBuildup value
  fill a hidden 0-100 meter (decaying 18/s); at 100 the enemy staggers for
  the skill's StunDuration and the meter resets. Every triggered stun stacks
  a permanent (per-life) 20% resistance onto that enemy — the second stun
  takes visibly longer to reach, the fourth barely ever comes. Bosses take
  half buildup and half duration. The chain-stun-to-death loop is dead.
  - Shield Bash: cooldown 0.7 -> **1.1s**, builds **60**/hit (2 hits to the
    first stun, 3 to the second, 4 to the third...).
  - Mace Strike **7**/hit, Mace Slam **15**/hit — flavor pressure that
    occasionally matters mid-brawl, never a stun engine.
  - Ground Slam builds **100** (its identity IS the AoE stagger — one slam
    stuns fresh enemies, but resist stacks stop repeats chaining).
- **You can SEE the buildup**: the top-of-screen hover display grows a
  golden STUN strip and an icy CHILL strip under the health bar (only while
  non-zero); Options -> Gameplay -> "Overhead Status Bars" additionally
  draws mini strips under enemies' small overhead health bars. Stun buildup
  replicates in enemy state snapshots (protocol v32).
- **Leveling steepens.** XP-to-next was linear (40 + 25/level); it now
  COMPOUNDS: each level's requirement is the previous grown 12% plus the
  25 flat step. L1 65 (unchanged feel), L10 550 (was 290), L20 2,146 (was
  540), L30 7,103, L40 22,501 — enemy XP left untouched, the wall just
  rises to meet it.

---

# Addendum (batch 45): cluster bash, damage-paid skill XP, skill curve

- **Shield Bash hits the whole impact cluster.** The impact-point archetype
  now strikes EVERY enemy inside the impact radius — knockback and stun
  buildup apply to each — instead of only the closest one. Bashing into a
  pack scatters and staggers the pack.
- **Skill XP follows damage, not killing blows.** Every point of damage a
  skill deals banks its proportional share of the enemy's XP value (clamped
  to remaining health — overkill pays nothing extra — and scaled by the
  level-gap factor). A level-1 spell can be trained late-game by USING it,
  no last-hitting required; DoTs (poison/bleed/burn) credit the skill that
  applied them; summon damage trains the summon skill. Character XP is
  unchanged and stays kill-based with party share. Per-hit grants batch
  into a ~1s character sync so the wire never floods.
- **Skill leveling compounds like character leveling**: XP-to-next was
  linear (60 x level); now previous x 1.15 + 30 on a 60 base — L1 60
  (unchanged), L5 255 (was 300), L10 715 (was 600), L15 1,679, L20 3,619
  (was 1,200). Early ranks stay quick; mastery is a commitment.

---

# Addendum (batch 46): QoL sweep + the gambler

- **Craft from the stash.** Enchanting Scrolls arm (right-click) and apply
  straight from the stash grid — scroll and/or target can live in the bag or
  the stash interchangeably (server validates chest reach). The armed-scroll
  cursor follows across both panels.
- **Drop from anywhere.** Releasing a drag over the world now drops items
  from the STASH and straight OFF equipped slots too (stats recompute, worn
  appearance rebroadcasts) — no bag round-trip.
- **Buy-back holds a whole level.** The 10-slot cap is gone: the counter
  keeps everything sold during the current character level and wipes on
  level-up; the Buy Back tab scrolls (mouse wheel) with a row counter.
- **Mouse 4/5 bindable.** The side buttons (XButton1/XButton2) are
  first-class bindings — capture, save ("Mouse:X1"), display ("M4"/"M5").
- **Skill levels drink deeper**: mana cost per skill level 10% -> 16%.
- **Forest sun pools are lumpy now**: each pool is three overlapping lobes
  orbiting, breathing and shimmering on independent periods — an organic
  morphing blotch instead of a perfect circle, with visibly livelier flicker.
- **Sable the Gambler** joins the sanctum (west wall). Pick the EXACT gear
  base from a scrollable table of everything your level can wear (36 bases
  at level 7); pay steeply (45 + 12/level gold, x1.5 for jewelry); rarity
  and mods are fate's: 30% normal / 50% magic / 20% rare, rolled at your
  character level. Prices and eligibility are shared client/server rules —
  no stock traffic. Protocol v33.
- **No more through-wall bolts**: ALL client-side projectiles (predicted
  ghosts and remote-player bolts alike) stop at walls locally instead of
  gliding through geometry until the server corrects them.

---

# Addendum (batch 47): the caravan stand (wagon defense) + party HP scaling

- **Enemies scale with the party.** Every spawn gains +15% max health per
  player beyond the first (`EnemyLevelScaling.HealthPerExtraPlayer`) — a
  full session never turns the same content trivial. Applies everywhere:
  arena, campaign, defense waves, elites, bosses.
- **A second door in the Sanctum.** The west wall (straight across from the
  run door) now opens on the CARAVAN STAND: a freshly generated defense
  arena (40x28) with three enemy portals (west x2, north), a caravan wagon
  parked right-of-center, and a workbench camp beside it.
- **The loop**: build phase -> ready up at the workbench -> a wave pours
  from the portals and marches on the wagon -> clear it -> build again,
  five waves total (6 + 4/wave + 3/extra-player enemies, trickling every
  1.1s, enemy level = zone level + 1/wave). Win: the exit unlocks and the
  caravan pays two boss-table loot showers plus a gold purse at the wagon.
  Lose the wagon (900 hp, +25%/extra player) and the Sanctum reclaims
  everyone empty-handed.
- **The workbench builds, and gold finally drains**: crossbow turret 60g
  (range 7.5, a bolt every 1.1s, damage scales +18%/zone level, kill credit
  to the builder — no skill XP); spiked barrier 25g (320 hp wall enemies
  must break through or go around — structures block ENEMY movement only);
  flamethrower turret 90g, LOCKED until its blueprint is found (next batch).
  Placement is click-to-place with a green/red ghost; the server re-checks
  reach (5.0), terrain, spacing (0.9, barriers can form walls) and portal
  exclusion (4.0).
- **Enemies fight the furniture**: anything standing in reach gets chewed
  with plain damage ticks (1s clock, no telegraphs — walls don't dodge);
  players or summons inside ~aggro range still pull normal fighting AI.
  March pathing rides a wagon-anchored flow field spanning the whole arena.
- **Brakka the Sellsword** waits by the wagon during build phases (steps
  out when the wave starts, returns after). He deals in mercenary
  contracts — a rare-drop currency arriving next batch alongside the
  researcher NPC and randomized mercenaries.
- Protocol v34 (DefenseState, StructureSpawn/Health/Remove, NpcRemove,
  BuildRequest).

---

# Addendum (batch 48): contracts, the researcher, mercenaries, the flamethrower

- **Mercenary Contracts** enter the loot stream as a rare Curio drop (1.2%
  per kill by default, 35% per boss roll; stacks of 10 in the bag) — the
  defense mode's secondary currency.
- **Odessa the Researcher** joins the Sanctum (west wall, south). One
  contract buys one RANDOMIZED hire: kind (warrior/archer), name and power
  (rides your character level) are all the server's roll. The roster is
  permanent and rides the character save.
- **Deploy mercs like turrets**: the defense workbench lists your roster —
  free to field during build phases, one outing each per run, placed with
  the same click-to-place ghost. They guard the chosen spot as owner-bound
  summons (warriors hold the line, archers shoot from it) — and unlike
  skeletons, a fallen merc stays down until the next run. They never
  follow you out of the arena; the roster keeps them regardless.
- **The Flamethrower Blueprint** is a rarer Curio still (0.3% / 8% boss).
  Hand it to Odessa and the flamethrower turret (90g, close-range fire
  spray, damage scales with zone level) unlocks for that character
  forever; the workbench row lights up accordingly.
- Sellsword/researcher NPCs never open gear stalls; merc sprites are the
  summon rig in flesh and leathers. Protocol v35 (ResearchRequest,
  DeployMercRequest).

---

# Addendum (batch 49): defenses on the grid — solid walls, cones, repair

- **Placement locks to the tile grid**: one structure per tile, the ghost
  snaps and shows the claimed tile as an iso diamond, and the wagon camp
  itself sits on the same grid.
- **R rotates while placing** (west/north/east/south). Barriers pick their
  wall AXIS and now draw as proper isometric fence segments that chain
  tile-to-tile into continuous spike lines — walls finally look like walls.
- **Walls actually stop enemies.** Structure tiles are hard collision for
  the horde (players and summons walk their own camp freely); the soft
  push-aside is gone. The wagon flow field treats built tiles as walls, so
  waves route around partial walls — and an enemy that is FULLY walled off
  turns on the nearest built piece and chews through it. A tank taunting
  from behind their own wall no longer stalls the AI: unreachable targets
  make enemies eat the wall between them.
- **Turrets are directional**: a 130-degree fire cone around the placed
  facing (crossbow and flamethrower both). Placement draws the exact cone
  and range — and every standing turret shows its coverage while you're
  placing, so camp planning is a real activity.
- **Workbench repairs**: one "Repair all" button patches every damaged
  structure (wagon included) at 4 gold per 100 missing hp — cheap upkeep,
  build phases only.
- Protocol v36 (rotation on the wire, RepairRequest).

---

# Addendum (batch 50): Arrow Rain, leaner health, deliberate selling

- **Base health 60 -> 35.** The flat freebie pool was drowning out attribute
  life — a warrior's Strength now reads as a real durability edge instead of
  a rounding error. Health-per-level (4) and STR life (2/point) unchanged;
  everyone is squishier, warriors least of all.
- **Arrow Rain** (bow attack, trainer-taught): mark a circle up to 9 tiles
  out; after a heartbeat a volley lands on everything inside (90% weapon
  damage +7%/level, radius 2.2 +0.08/level, 4s cooldown, 10 mana). The
  wind-up draws the target ring with arrows streaking down; the landing
  leaves shafts stuck in the dirt.
- **Selling is deliberate now**: with a merchant open, bag items sell via
  CTRL+click or by dragging them onto the shop window — a plain click just
  starts a drag, so misclicks stop costing gear.
- **Ctrl+click quick-move**: with the stash open, ctrl+click shuttles items
  bag->stash and stash->bag instantly (server auto-places).
- Dev aids: give_bow/learn/give_gold/give_curio debug commands.

---

# Addendum (batch 51): stair-aware melee, rain volleys, the supply economy

- **Melee connects across stairs.** The flat height gate (±0.75) whiffed on
  anyone a step above or below. Swings now walk the ground profile between
  attacker and victim: a continuous slope (ramp/stairs, up to one full level)
  connects; a sheer cliff or a bridge deck still blocks. Applies to every
  melee archetype AND to enemy swings — no stair-immunity cheese either way.
- **Arrow Rain rework**: damage lands WHILE arrows land, not after. The cast
  is a short 0.35s draw (ring telegraph), then a rain window: three strike
  ticks 0.2s apart sync with the falling volley, each enemy clipped once per
  volley (same damage budget as before), and bodies wandering in mid-fall
  still get caught. The sky ignores terraces — every elevation inside the
  circle is hit. Ground bursts (Arcane Burst) also now use the TARGET
  point's ground height, so casting up onto a ledge works.
- **Supplies replace gold in the arena.** Building and repairs now spend a
  run-scoped currency: 120 to start, +5 per wave kill (turret/merc kills pay
  their owner), +35 per cleared wave. It's granted on entry, dies with the
  run, and never touches your gold. Prices: crossbow 60, barrier 20 (was
  25g), flamethrower 90; repairs 4 supplies per 100 missing hp.
- **Build capacity** (Dungeon Defenders style): a shared placement budget of
  30 (+8 per extra player). Crossbow 5, barrier 2, flamethrower 6. Destroyed
  structures hand their share back; the workbench shows supplies, capacity
  and per-row costs.
- Protocol v37 (per-player supplies ride DefenseState); give_supplies debug.

---

# Addendum (batch 52): rolled footprints, carved arenas, highlands + bridges

- **Map footprints roll from the seed.** Forest runs now span 86-130 x 26-36
  tiles, defense arenas 38-64 x 28-44 — no more one-size rectangles. Clients
  rebuild the identical map from the shared seed (protocol v38).
- **The defense arena is carved, not boxed.** Every stand is a cavern cut out
  of solid rock: the camp opens against the east wall at a random height,
  3-5 portals (rolled per run) open along the west/north/south edges in
  fresh spots, and each mouth carves a winding lane toward the camp with
  chambers swelling off it. Rock clumps sprinkle back in as cover and
  choke-anchors. Guarantees kept: every portal has a walkable lane to the
  wagon, the camp stays flat and clear, the path to the exit door stays open.
- **Forest runs got real geography.** The narrow terrace strips are gone;
  in their place rise HIGHLAND MASSIFS — big jittered-edge landmasses (some
  spanning the hall, some lobing off a side wall, ~35% wearing a level-2
  crown). The corridor climbs them by stair flights graded into the cliffs
  or slices a ground-level CANYON straight through. BRIDGES span the gaps
  between neighboring massifs and vault the canyons — cross on the deck
  while others pass beneath. Massif tops carry their own pack anchors, so
  the high ground is contested, not scenery.
- Safety nets unchanged and re-verified across seeds: spawn-to-boss path,
  no stranded pockets (stranded tree footprints now rockify too), no orphan
  or glitch stairs, same-seed determinism tile for tile.

---

# Addendum (batch 53): pets, the defense boss, honest AoE

- **Pets — the game's first UNIQUE items** (brown-gold, worn in a new Pet
  slot, visible companion in the world, rarest ordinary find: 0.15% per
  kill, 2% per boss roll, coinflip between the two):
  - *Nib, the Gutter Rat*: +4% move speed, and it physically fetches
    dropped gold within 9 tiles of you — server-authoritative pickup,
    banked hands-free. No other effects.
  - *The Vagrant Grimoire*: +2% mana regeneration, plus ALWAYS exactly two
    random tier-1 suffix modifiers rolled at drop time (any suffix family;
    never re-rollable — pets can't be enchanted).
  - Pets are pure companions: never targeted, never damaged, never in the
    enemy's math. F1 debug: "Drop Pets" (real rolls at your feet) and a
    give_pet command.
- **The Caravan Stand has a boss now**: the final wave opens with the
  GRAVELORD striding out of a random portal — boss loot table, slam that
  cracks structures, one more level than the wave. F1 "Skip To Final Wave"
  jumps a build phase straight there.
- **Enemy AoE damages the camp**: a grave caller's ground burst and any
  boss slam now scorch turrets, barriers and the wagon in their circle —
  structures are no longer immune to everything but chewing.
- **Arrow Rain, per-arrow hitboxes**: all 12 arrows are individual impacts
  at deterministic spots and moments (shared math with the animation — what
  you see land is what hits). Each arrow deals 40% of the skill's roll in a
  0.8 radius; one body can be struck by several arrows, clusters eat the
  whole volley, and gaps in the scatter genuinely miss.
- Protocol v39.
