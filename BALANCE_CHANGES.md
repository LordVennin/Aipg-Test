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
