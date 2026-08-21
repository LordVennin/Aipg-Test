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
