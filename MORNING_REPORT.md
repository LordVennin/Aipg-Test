# Morning report — Batch 85 overnight

## Summary

Everything in the plan was reached: Phases 0-3, the Phase 4 soft-lock audit and the
Phase 5 handoff. The build is at 0 warnings and the headless suite went from **841 to
1056 checks**, green. The game now writes a log and crash reports, survives a throwing
server tick, saves atomically with a backup and autosaves, repairs bad characters on
join, validates its data, and refuses mismatched builds at the handshake. On top of
that: enemy populations are data; seven new enemies (goblins and bandits) own the
Mirewood; a fourth portal zone (the Scorched Bluffs); skill books and a reworked
trainer; a mace identity (Crash + three skills + four modifiers + a scroll); three bow
skills + three bow modifiers; and a per-character quest tracker with no catch-up.

Three things I want you to know before anything else:

1. **Two real crashes existed before tonight and are fixed.** (a) Any exception in the
   server tick killed the whole process with no save — now guarded. (b) More than ~51
   live enemies made the enemy snapshot exceed LiteNetLib's unreliable packet limit,
   which THROWS; goblin packs reach that easily. Snapshots are now chunked. The new tick
   guard is what caught (b).
2. **The old test suite is flaky on its own** (3 of 6 untouched baseline runs failed):
   a handful of old checks roll real dice. I did not touch them; I rerun on failure.
   Details in the decisions log.
3. **The work is on branch `ccr-06103ad7-ual5x5`, not `batch-85-overnight`** — this
   cloud session can only push to that branch. Nothing was committed to main.

Clock: started 08:51 UTC, finished about 11:00 UTC by the container's clock — the work
went much faster than the 8-hour budget, so nothing had to be cut for time.

## Commits

| Hash | Task | Status |
|---|---|---|
| `988e3dc` | 85.1-85.9: Phase 1 (logging, crash handlers, tick guard, atomic saves, autosave, join sanitization, data validator) + Phase 2 (fingerprint handshake, coalesced character sync) | done, green |
| `09f7157` | 85.10: data-driven enemy pools per theme (3.1) | done, green |
| `62bae4c` | 85.11: bandits and goblins (3.2) + snapshot chunking + kiting AI fix | done, green |
| `da15601` | 85.12: skill books and the trainer (3.3) | done, green |
| `8c76322` | 85.13: mace identity (3.4) | done, green |
| `c8fbe96` | 85.14: bow skills (3.5) | done, green |
| `e5ee03d` | 85.15: the Scorched Bluffs (3.6) | done, green |
| `6ffde34` | 85.16: quest tracker (3.7) | done, green |
| `38532b8` | 85.17: soft-lock audit (4.1) | done, green |
| (last commit) | 85.18: handoff — CLAUDE.md, README, this report, SimVersion 2, Captain tuning, review fixes | done, green |

Nothing was reverted. Nothing was skipped. See "Not reached / left for you" for what
was deliberately left alone.

## Nettest: before -> after

| | Checks | Wall time |
|---|---|---|
| Baseline (Phase 0) | 841 | ~65 s |
| After Phases 1-2 | 897 | ~66 s |
| After 3.1 / 3.2 / 3.3 | 906 / 933 / 959 | ~69-77 s |
| After 3.4 / 3.5 / 3.6 / 3.7 | 985 / 1001 / 1008 / 1038 | ~78-85 s |
| After 4.1 (final) | **1056** | ~100 s |

`--nettest b85` runs only the 215 new checks (~35 s). Versions now:
**ProtocolVersion 51, SimVersion 2.**

## What was added, with numbers

Full detail (pools, weights, every tier) is in BALANCE_CHANGES.md under "Batch 85".

**Enemies** (none undead, all bleed red, all keep their native level in lower zones)

| Enemy | Lvl | Life | Speed | Damage | XP | What it does |
|---|---|---|---|---|---|---|
| Goblin Skulker | 3 | 26 | 3.5 | 8 Slash | 6 | fast lunge (0.28 s wind-up); packs of 3-4 |
| Goblin Slinger | 3 | 20 | 2.7 | 8 Blunt | 7 | stones from 6.5 tiles, keeps 3.5 away |
| Goblin Shaman | 4 | 34 | 2.2 | 15 Fire cast | 14 | telegraphed fire burst (radius 1.7, 1.1 s), rare |
| Bandit Cutthroat | 4 | 62 | 2.9 | 17 Slash | 14 | slow blade (0.55 s wind-up); 50% bleed 3 dps / 4 s |
| Bandit Archer | 4 | 40 | 2.6 | 12 Thrust | 13 | arrows from 7.5 tiles, keeps 4 away |
| Bandit Brute | 5 | 130 | 2.0 | 20 Blunt | 24 | 0.75 s wind-up; a landed hit shoves you 2.2 tiles |
| The Bandit Captain | 5 | 450 | 2.5 | 16 Slash + 6 Blunt | 100 | boss: dash 26 dmg, 35% bleed, calls 2 bandits every 30 s |

**Mace** — Crash: knockback into a wall / structure / another enemy = +40% of the hit
and +35 stun buildup (20% / +15 to an enemy it was thrown into).

| Skill | Numbers |
|---|---|
| Leap Slam | leap <= 5.5 tiles over 0.45 s (invulnerable), 110% weapon dmg in radius 2.0, knockback 1.8, 5 s cd, 12 mana |
| Skull Crack | one target, 170% weapon dmg, 85 stun buildup; vs stunned/frozen +100% dmg and +1.2 s stun; 2.2 s cd, 10 mana |
| Fault Line | 90% weapon dmg slam, then 3 eruptions (1.5 tiles apart, 0.25 s apart, 80% each, radius 1.2); 3.5 s cd, 14 mana |
| Aftershock (scroll, `Slam` tag) | the slam repeats 0.6 s later at 50% damage |

| Mace modifier | Effect | Tiers (ilvl 1 / 10 / 20 / 30) |
|---|---|---|
| Concussive (prefix) | % stun buildup | 10-16 / 17-24 / 25-33 / 34-44 |
| Seismic (prefix) | % area of Slam skills | 6-10 / 11-15 / 16-21 / 22-28 |
| Crushing (prefix) | % damage vs stunned/frozen | 12-18 / 19-27 / 28-38 / 39-50 |
| of Impact (suffix) | % knockback and Crash damage | 8-14 / 15-22 / 23-31 / 32-42 |

**Bow**

| Skill | Numbers |
|---|---|
| Snipe | chargeable (0.9 s): 130% weapon dmg; full draw +150% dmg, +60% range, +80% speed and pierces all; 1.8 s cd, 9 mana |
| Split Arrow | 5 arrows 9° apart, 45% weapon dmg each, range 5.5; 0.9 s cd, 8 mana |
| Disengage | hop 3 tiles back (i-frames) + an 80% arrow that slows 2.5 s; 4 s cd, 6 mana |

| Bow modifier | Effect | Tiers |
|---|---|---|
| Piercing (prefix) | arrows pass through +N enemies | +1 (ilvl 1), +2 (ilvl 20) |
| Barbed (prefix) | % bleed chance with bow attacks | 8-12 / 13-18 / 19-25 / 26-34 |
| of the Hawk (suffix) | % projectile speed and range | 8-12 / 13-18 / 19-25 / 26-34 |

**Skill books** (9): Hammer and Anvil (Mace Strike, Mace Slam, Ground Slam),
Shieldbearer's Drill (Shield Bash, Skull Crack), Earthshaker's Creed (Leap Slam, Fault
Line), Fletcher's Manual (Arrow Shot, Arrow Rain, Split Arrow), The Long Draw (Piercing
Shot, Snipe, Disengage), Grimoire of Embers (Fire Bolt, Arcane Burst), Rime Codex (Ice
Spike), Storm Letters (Chain Lightning, Arcane Burst), Ossuary Rites (both summons).
Boss table: 30% per roll for a random book. Bandits 1.2-3% for bow/shield/mace books,
Goblin Shamans 3% each for the three spell books. First Barrow Lord / Codex kill you
are present for: one guaranteed book, reserved for you, until you pick one up.

**Zone**: the Scorched Bluffs (`warp_zone_arid`) — a bandit stronghold on the arid theme.

**Quests** (7): The Old Road -> A Word with the Lorekeeper -> Under the Ruins -> The
Sealed Way; side quests Words on Paper, Thin the Warrens (12 goblins), The Captain's Head.

## Play this first (30 minutes)

Build and run from `ARPG/`: `dotnet run`. Check the bottom-right of the main menu shows
`data xxxxxxxx · protocol 51 · sim 2` and no red banner.

1. **Story, new character, pick the Warrior** (the mace work is the biggest change).
   Top right is the quest tracker: "The Old Road — Fell the Barrow Lord at the gate".
   Press `J` for the quest log.
2. **Walk the road and kill the Barrow Lord.** A teal-labelled skill book drops for you
   (*Hammer and Anvil*, *Shieldbearer's Drill* or *Earthshaker's Creed*). Pick it up.
3. **In the ruins camp**, talk to the skill trainer (`F`). The quest "A Word with the
   Lorekeeper" completes. In the panel press **Hand over** next to the book; its skills
   unlock; buy one (75 gold — the quests have paid you by now). Locked skills show
   "Requires: <book>".
4. **Take the stairs down, clear the archive, kill the Gilded Codex.** A second
   guaranteed book drops, plus the sealed scroll. Pick the scroll up (the quest wants
   YOU to pick it up — try leaving it once if you want to see it drop again next visit).
5. **Put the scroll on the podium, step through the portal: the Mirewood.** Goblin
   packs (small, green, fast; slingers hang back; the shaman's fire circle is the red
   one to walk out of) and bandits (the hooded ones with drawn swords; archers kite;
   the big bare-armed Brute shoves you). Room three: the **Bandit Captain**.
6. **What to try with the mace:** stand so an enemy is between you and a wall and
   Mace Slam it — the orange "Crash N" number and the dust puff are the new mechanic.
   Leap Slam into a pack next to a wall. Stun something (the yellow bar) and then Skull
   Crack it for double damage. Fault Line down a corridor.
7. **Bow (second character, or F1 -> give bow + `learn_skill snipe`):** hold Snipe to
   full and fire down a line of enemies; Split Arrow point blank; Disengage when a
   Brute winds up.
8. Shortcuts if you only have ten minutes: `ARPG_STORY=1 ARPG_DEVUI=run:warp_zone_forest+warp_boss dotnet run -- --sp`
   drops you straight into a Mirewood run; `run:warp_zone_arid+warp_boss` is the new
   zone; `ARPG_DEVUI=books` scatters every book; `ARPG_DEVUI=spritesheet` shows the
   new sprites; `ARPG_ARENA=1 ARPG_DEVUI=bandits` (or `goblins`) rings you with one of each.

**What still looks rough**

- Leap Slam has no jump arc: you slide fast to the spot and slam. Disengage's hop is
  the dodge roll played backwards-ish. Both work; neither is pretty.
- The Brute's shove snaps you to the new spot (a teleport, not a slide).
- Bandit sprites are plain: hood, jerkin, a weapon per role. The Captain reads well
  (red hat and plume); the Cutthroat and Archer are similar at a glance until you see
  the sword or the bow.
- Player bleed has no icon — you see red damage numbers ticking on yourself.
- The quest tracker's "helping <name>" line and the trainer panel have only been seen
  in screenshots and tests, not played with a second human.
- Balance is first-pass. The Captain was eased once tonight (level 6 / 520 hp ->
  level 5 / 450 hp) because he is now the first portal boss a new character meets.
  Goblin packs of 4-6 may be a lot for a fresh level-4 character.

## Testing co-op on this build

1. Both machines need **this exact build** (same commit, same `Data/` folder). On each,
   the main menu's bottom-right line must match character for character:
   `data xxxxxxxx · protocol 51 · sim 2`. If the 8 hex characters differ, the data
   files differ and the join will be refused with "Game data differs from the host —
   both players need the same build".
2. Host: Host Game (story). Joiner: Join Game -> the host's IP. Old saves are fine —
   known skills stay unlocked.
3. Things worth checking together: each of you gets your OWN guaranteed book from the
   Barrow Lord (try taking the other's — "That one is meant for <name>"); a book
   handed to the trainer unlocks for the one who hands it over only; the quest tracker
   shows each of you your own step; if the joiner arrives after the Codex is dead, the
   joiner's log starts at the beginning (no catch-up) and the host's tracker says
   "helping <joiner>".
4. If the host hits a simulation error it no longer vanishes: the host's
   `Logs/scrollbound-<date>.log` has the stack, and after 30 failing ticks in a row
   everyone is dropped to the menu with a message.

## Decisions log

- **Branch:** the plan asks for `batch-85-overnight`, but this cloud session is only allowed
  to push to `ccr-06103ad7-ual5x5`. All work is on that branch (off the default branch, never
  main). Rename/merge it as you see fit.
- .NET 8 SDK wasn't in the container; installed `dotnet-sdk-8.0` from apt (8.0.134).
- **Baseline is flaky.** I ran the untouched baseline suite repeatedly: 3 of 6 baseline
  runs failed 1-2 *old* checks that depend on dice (`Mace Slam slows survivors` is a 60%
  roll, `overlook spitter fires a descending shot`, `five waves beaten: the Last Stand is won`, `enemies drop gold`,
  `Shattering bursts 5 ice shards out behind the struck enemy`,
  `warriors close to melee range and hit`).
  The server RNG is unseeded. I did not change those checks; when one of them fails I
  rerun (up to 3 attempts) and only treat a failure as real if it repeats or is in a
  batch 85 check. Worth a seeded-RNG pass some day.
- **Commit granularity:** Phases 1 and 2 touch the same handful of files
  (`GameServer.cs`, `Program.cs`, `GameData.cs`), so they landed as one commit instead of
  nine; build + full nettest were run on the combined result. From Phase 3 on it is one
  commit per task.
- `--nettest b85` runs only the batch 85 sections (fast iteration); plain `--nettest`
  runs everything.
- The game already had a 30 s periodic save in `PlayScreen`; it is replaced by the new
  `Autosaver` (60 s / map change / level-up, background write) as the plan specifies.
- The data validator's schema was written up front for the Phase 3 content (pack pools,
  skill books, quests), so the model classes for those exist from this commit on.
- Warp zone seals now carry `ZoneThemeId`/`ZoneName` in `Data/Modifiers/warp.json`
  (the 3.6 "move the switch into data" step, pulled forward because the validator checks it).

- **3.1 pools:** pack pools apply on **portal runs**; the test-grounds campaign loop and the
  story road always use the built-in undead lists (so a forest-themed test run stays
  undead, as the plan asks). Gate semantics: an entry with `MinLoop`/`MinLevel` opens when
  EITHER is reached (that is today's "knights from loop 2 or level 4" rule). Extra theme
  fields beyond the plan: `OverlookPool` (high-ground pickets), `PromisedPacks` (the
  guaranteed Barrow Knight patrol) and `BossPacks` as a list (early/late guard sets).
  Survival/defense waves on graveyard/tomb keep the hard-coded per-wave table (it has
  per-wave percentages a flat weighted list can't express exactly); themes that list
  `WaveEnemies` use those.
- Boss messages now name the actual boss ("The Gilded Codex falls — the way home is
  open" instead of always "The Gravelord").
- New debug command `enter_portal <seals>` (story worlds): straight into the portal run
  a scroll would open.

- **3.2 enemy level floor:** new enemies carry `MinSpawnLevel` (= native level) so a
  level-1 Codex scroll doesn't field "level 1" bandits with level-4 stats and level-1 XP.
  Undead are unaffected (their value is 0).
- **3.2 "bleeds via a high-damage telegraphed swing":** I read this as the Cutthroat's
  slow, heavy swing leaving a bleed on the player. Player-side bleed did not exist; it is
  a small addition (half-second ticks that ignore armor), not a new AI system. The
  Brute's "big knockback" likewise needed a player shove: the server moves the player
  (walls stop it) and snaps the client with the existing respawn/teleport packet.
- **3.2 kiting AI fix:** while testing, ranged enemies with `KeepDistance` turned out to
  never attack while a player stood inside their comfort ring next to a wall. They now
  loose a shot whenever it is ready while backing off. This also changes Frost Tomes.
- **Pre-existing crash found by the new tick guard:** `EnemyStates` went out as ONE
  unreliable packet; LiteNetLib throws above ~1 KB, i.e. above ~51 live enemies. Goblin
  packs push portal runs past that. Before tonight that exception would have killed the
  server process. Snapshots are now chunked (40 entities per packet).
- **Art check:** the game boots under `xvfb` in this container, so the new sprites were
  looked at for real. New dev hooks: `ARPG_DEVUI=spritesheet` (every bandit/goblin frame
  and downed pose), `bandits`, `goblins` (ring the player with one of each; debug
  `spawn_family <family>`).

- **3.3 where skills are gated:** in **story worlds only**. The plan says "Test grounds keep
  everything unlocked"; the test-grounds campaign (sanctum hub) has a trainer that dozens
  of existing checks buy from, so both it and the arena stay ungated. The sanctum trainer
  still accepts books.
- **3.3 books for start skills:** Arrow Shot and Mace Strike are also in *Fletcher's
  Manual* / *Hammer and Anvil* (Fire Bolt is in *Embers*), otherwise another class could
  never learn them now that the trainer no longer sells everything.
- **3.3 reserved guarantee books:** "each player picks up their own" is enforced: the
  guaranteed book is reserved for its player on the server (someone else gets "That one is
  meant for <name>"); if that player has left, anyone may take it. Once picked up it is an
  ordinary tradeable item.
- **3.3 Rime Codex** teaches a single skill (Ice Spike is the only cold skill).
- ProtocolVersion is now **49** (new `SkillBookTurnInRequest`, `ZoneState` carries
  `SkillsGated`).
- Dev hooks: `ARPG_DEVUI=books` (one of each book on the ground), `teach` (opens the
  trainer panel), `book:<id>`; debug `drop_books`, `give_book <id>`.

- **3.4 Leap Slam movement** follows the game's existing model: the CLIENT predicts the jump
  (same code path as the Shield Bash lunge, stretched over the wind-up) and the server
  lands the slam wherever the leaper is when the wind-up ends, with i-frames in between.
  The nettest stands in for the client's jump by moving the client player to the landing.
- **3.4 Crash into another enemy** means: thrown far enough that it ends up overlapping an
  enemy that was AHEAD of it along the throw. A tight ring of enemies all thrown outward
  does not crash into itself.
- **3.4 Aftershock vs knockback:** the repeat lands where the slam landed; enemies the slam
  threw clear are out of it. It pays off against walls (which hold them in), fresh
  arrivals and bosses. Left as is — flag it if you would rather the repeat follow the victims.
- ProtocolVersion **50** (the damage event carries the Crash flag for its own number tint).

- **3.5 Disengage's hop** is client-predicted like the dodge roll (the server grants i-frames
  and broadcasts the roll); the headless test checks the server half and the replicated
  dodge event, not the local movement.
- **3.5 Piercing Shot moved** from *Fletcher's Manual* to the second bow book (*The Long
  Draw*: Piercing Shot, Snipe, Disengage) so each bow book keeps three skills.
- **3.5 "+1 pierce"** is a flat stat, so *Piercing* has two tiers (+1, +2 at item level 20)
  rather than four percentage bands.

- **3.6 arid check:** captured an arid run under xvfb — sand/cracked ground, bluff walls,
  skull and rock clutter all read fine. The run generator scatters tall grass on every
  theme; on arid maps it is now painted as dry scrub (render-only, no generation change).
  Dev hook `ARPG_DEVUI=run:<seal+seal>` (with `ARPG_STORY=1`) drops straight into a run.

- **3.7 quest order:** the plan lists "reach the camp -> fell the Barrow Lord"; in the game
  the Barrow Lord guards the gate BEFORE the camp, and steps are ordered, so the first
  quest is "fell the Barrow Lord" then "reach the camp". The story line is four chained
  quests rather than one long one, so each pays out as it lands.
- **3.7 step type `collect`** (not in the plan's list) carries the "quest items are never
  auto-added" rule: "Under the Ruins" ends with picking the Codex's scroll up yourself.
  Leave it and the step stays open; the Codex is there again on the next visit and drops
  a scroll reserved for you.
- **3.7 where quests run:** story worlds only (the quest line follows the story; the test
  grounds have no tracker).
- **3.7 `talk`** needed a new tiny request (`TalkRequest`): NPC panels used to open purely
  client-side, so the server never knew a conversation happened.
- **3.7 "finish a portal run"** = the party walks out through the run's final exit (which
  only opens once the boss is dead / the waves are beaten). A debug warp home or a party
  wipe is not a finish.
- **3.7 NO CATCH-UP (behaviour change, as the owner decided):** `AdoptStoryProgress` no
  longer writes the host's story flags into a joiner's saved `StoryProgress`. The world
  still follows the host (same map, scrolls unlocked, portal available). A joiner's saved
  flags now change only from what happens while they are present: standing in the camp
  sets `ReachedCamp`; being there for a Codex kill (any Codex kill, not only the world's
  first) sets `CodexFelled`/`ScrollsUnlocked`. Consequence to be aware of: a character who
  only ever joined a far-advanced host will, when they later HOST their own story world,
  start it from wherever their own record says — typically the road.
- **3.7 tracker placement:** there is no minimap in the game, so the tracker sits in the
  top-right corner. Quest log: `J` (rebindable, Options -> Controls).
- ProtocolVersion **51** (`TalkRequest`, `QuestStatus`).

## Soft-lock audit (Phase 4.1)

| Situation | Finding | Result |
|---|---|---|
| Solo death on each map kind (road, ruins, archive, forest/arid/survival/defense portal runs; sanctum, test forest run, caravan stand, old road) | Road maps respawn you on a timer; everywhere else a solo death is a party wipe and brings you home alive. | OK, now covered by checks |
| Whole-party wipe in a portal run | Everyone wakes in the camp; the scroll is burnt. | OK |
| Is the first warp scroll guaranteed? | Yes (the world's first Codex kill). **But** a wiped first run left "finish a portal run" open with no scroll and only random drops to fix it. | **Fixed:** the Codex gives another scroll (reserved) to a present player whose portal quest needs one and who carries none |
| Selling your only weapon | Possible: a warrior or archer could sell (or drop and abandon) their only weapon and have no usable skill. | **Fixed:** the merchant refuses the last weapon; and a character with no weapon anywhere is handed their class's starting weapon on joining or on coming home |
| Leaving a map without a quest item / guaranteed book | Step stays open, guarantee stays unspent, source is there again. | OK (designed in 3.3 / 3.7, covered by checks) |
| A player disconnects while others are readied at a door | **Bug:** the leaver's ready mark stayed and nothing re-evaluated the door: one ready player waited at "1/1" until they re-pressed the key. Same at the caravan workbench. | **Fixed:** leavers are cleared from every ready set and the door opens if everyone still here was ready |
| A dead player's only living teammate disconnects | The wipe rule already catches it. | OK, now covered |
| A player disconnects mid-cutscene | Scenes are client-side; nothing waits on them. | OK |
| A joiner arrives mid-cutscene / mid-defense-wave / while the exit is sealed | Gets the map, structures, starting supplies, the seal state and the boss. | OK, now covered |
| (found on the way) A leaver's summons | They stayed in the world ownerless. | **Fixed:** despawned with their owner |

## Behavior changes to existing checks

- `the sealed warp ... warp pool` check counted exactly 3 `warp_zone` seals; now 4.
  The modifier-count check also sets the new zone seal aside.
- `tiered modifier database loaded (482 modifiers)`: pinned the exact modifier count.
  Now asserts the original database is still 482 entries once the new mace/bow-only
  groups (`mace_*`, `bow_*`) are set aside.
- `every graveyard enemy is undead ...`: used to assert that EVERY non-tome enemy in the
  data is undead. Now asserts the four graveyard sprite families are undead, and that
  bandits/goblins are not, bleed, and never spawn buried.
- `room three: the Gravelord waits ...` / `the Gravelord falls and the last room's exit
  leads home` (story, the Codex's Mirewood scroll): the Mirewood's boss is now the
  Bandit Captain; both checks look for `bandit_captain` (and that no Gravelord is there).

No check was deleted.

## Not reached / left for you

- **Nothing in the plan was skipped or reverted.**
- Finding #8 in the plan (any peer can run debug commands) was left alone, as instructed.
- Not done because the plan parks them for later: a zone graph instead of magic
  `MapIndex` ints, splitting `HeadlessNetTest.cs`, composable enemy abilities, gear
  tiers past 35, server-side movement checks.
- Not done, worth a look: seeding the server RNG in the test harness (the flaky old
  checks); a jump arc for Leap Slam; a bleed icon for players; `SpriteGen` seeds
  cosmetic detail with `string.GetHashCode()` (per-process, so two clients can draw
  different dents on the same enemy — cosmetic only, pre-existing).
- No human has played this build. Everything above is from the headless suite and
  from screenshots taken under xvfb.
