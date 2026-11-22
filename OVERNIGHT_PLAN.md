# Overnight plan — Scrollbound Batch 85: hardening + new content

You are working unattended overnight on `LordVennin/Aipg-Test` (Scrollbound, a C#/MonoGame
isometric co-op ARPG, ~35k lines, LiteNetLib, host-authoritative). The current build works.
The game is an early test build that is only really playable to about level 10. Tonight's
goal: make it safe to play (no lost progress, no silent crashes), then add content that
extends the game. Nobody will answer questions tonight: make reasonable calls, write them
down, keep going.

Run `date` now and again between tasks. Budget roughly 8 hours. There is more here than one
night fits. Work strictly in order. Don't start a new content task with less than 90 minutes
left. Reserve the last 45 minutes for Phase 5 no matter what. Anything not reached goes on the
report's "not reached" list. That is an acceptable outcome.

---

## What a review found (context for Phases 1–2)

The foundation is solid: clean server/client split (the render thread never touches server
state), every gameplay action is a validated request, content is JSON-driven, and
`--nettest` has ~840 checks. The data files cross-reference cleanly today. The risks:

1. **A server exception kills the whole game with no save.** `GameServer.RunLoop` calls
   `Update` → `World.Tick` with no try/catch on a background thread, which terminates the
   process in .NET. Packet handlers are guarded; the tick is not.
2. **Progress is only saved on Save & Exit, app close, or disconnect.** `Json.SaveFile` is a
   plain `File.WriteAllText`, so a crash mid-write corrupts the save, and there's no backup.
3. **Diagnostics are invisible.** `OutputType` is `WinExe`, so every `Console.WriteLine`
   goes nowhere on Windows.
4. **A broken JSON file silently deletes content.** `GameData.LoadAll` skips files that fail
   to parse, and duplicate ids silently overwrite.
5. **Version check doesn't cover map generation.** Clients regenerate maps from a seed, but
   `ProtocolVersion` (47) wasn't bumped when batches 82–84 changed `GameMap.cs` and barrier
   collision, so mismatched builds connect and silently desync.
6. **Every kill sends the full character JSON to every party member** (`OnEnemyKilled` →
   `GrantCharacterXp` always true → `CharacterChanged`) plus an appearance broadcast.
   A dirty-flag batch already exists for skill XP (`SkillXpDirty`); extend it.
7. **Join trusts the client's character JSON.** An item whose base id no longer exists makes
   `GetBase` return null, and many paths dereference it → tick exception → see #1.
8. Any peer can run debug commands. **Intentional for now; leave it alone.**

---

## Ground rules

- New branch `batch-85-overnight` off the default branch. Never commit to or merge into
  main/master. Commit after each task, repo style: `Batch 85.N: <what changed>`. Push the
  branch if a remote is configured; never force-push.
- After every task: `cd ARPG && dotnet build` succeeds with no new warnings, and
  `dotnet run -- --nettest` exits 0. Record the check count.
- A task that isn't green within ~45 minutes (content tasks: ~75) gets reverted to the last
  green commit, explained in the report, and skipped.
- Every new behavior gets `--nettest` checks in the existing `Check(...)` style. If you
  deliberately change behavior an existing check asserts, update that check in the same
  commit and list it in the report. Never delete a check just to get green.
- Don't rewrite or split `ServerWorld` wholesale. New systems may go in new
  `partial class ServerWorld` files (`ServerQuests.cs`, `ServerSkillBooks.cs`).
- Numbers live in JSON (or `Defense.cs`/`DefenseBalance.cs`). Every new enemy, skill and
  modifier gets a short entry in a "Batch 85" section at the end of BALANCE_CHANGES.md.
- Any wire format change bumps `ProtocolVersion`. Any map generation, collision or
  simulation-rule change bumps `SimVersion` (added in 2.1).
- Never use `string.GetHashCode()` for anything that must match across processes (it's
  randomized per process). FNV-1a already exists in `ServerCharacterOps.cs`.
- Visuals: reuse existing effects, sprite code and sounds wherever possible. All art is
  procedural (`Render/SpriteGen.cs`, `TextureGen.cs`). New content needs to read clearly,
  not look polished.
- Everything is headless. Don't launch the GUI expecting input.
- Keep a running `MORNING_REPORT.md` at the repo root as you go, so a crash or context reset
  still leaves a record.

---

## Phase 0 — Baseline (≤20 min)

Build, run `--nettest`, record pass count and wall time in MORNING_REPORT.md. Skim
`Server/ServerWorld.cs` (Tick, SetupForest, TransitionTo, DoorReady, OnEnemyKilled),
`Server/ServerScrollRuns.cs`, `Server/ServerCharacterOps.cs` (LearnSkill, Research),
`Server/GameServer.cs`, `Net/GameClient.cs`, `UI/TrainerUI.cs`, `UI/ResearcherUI.cs`,
`Data/GameData.cs`, `Data/Zones/themes.json`, `Data/Enemies/enemies.json`,
`Data/Skills/skills.json`.

## Phase 1 — Never lose progress, never crash silently (~1.5 h)

**1.1 Logging.** `Util/Log.cs`: writes `Logs/scrollbound-<date>.log` next to the exe (keep
the 5 newest), thread-safe, flushes per line. Tee `Console.Out`/`Console.Error` into it at
startup so existing `Console.WriteLine` calls land in the file unchanged.

**1.2 Crash handlers.** In `Program.Main`, `AppDomain.CurrentDomain.UnhandledException` and
`TaskScheduler.UnobservedTaskException` write `Logs/crash-<timestamp>.txt` (exception, stack,
last 200 log lines) and attempt an emergency save of the local character
(`PlayScreen.SaveLocalCharacter`, guarded so it can't throw).

**1.3 Server tick guard.** try/catch around the body of `GameServer.Update`. On exception:
log it (full stack the first time, then a count for repeats of the same stack), skip the
rest of the tick, keep running. After 30 consecutive failing ticks, stop cleanly: broadcast
"Host simulation error — see the host's Logs folder", disconnect peers (clients already save
on disconnect), and return the host to the menu with that message. Add debug command
`throw_in_tick`; nettest that one bad tick doesn't crash and clients stay connected.

**1.4 Atomic saves with backup.** `Json.SaveFile` writes `path.tmp`, flushes, then
`File.Replace(tmp, path, path.bak)` (or a move when `path` doesn't exist yet).
`LoadCharacter` falls back to `.bak` if the main file fails to parse and logs it. Same for
`settings.json`. Nettest: corrupt the main save → loads the backup.

**1.5 Autosave.** The client saves its last server-sent `MyCharacter` every 60 s, on every
`MapChange`, and on level-up. Serialize on the render thread, write on a background task,
never two concurrent writes to one file.

**1.6 Join sanitization.** In `ServerWorld.AddPlayer`, before anything dereferences items:
null-coalesce every collection on `CharacterData` (including anything you add tonight:
unlocked skills, quest log, etc.); drop items with unknown `BaseItemId`, modifiers with
unknown ids, and skills/hotbar entries/passives with unknown ids (log each); clamp
nonsense values (negative gold/level, stacks over `MaxStack`). Nettest with a bogus item,
modifier and skill.

**1.7 Data validator.** `Data/DataValidator.cs`, run after loading: files that failed to
parse (make `LoadAll` record them), duplicate ids, enemy `LootTableId` exists, class
`StartWeapon`/`StartSkill` exist, modifier `StatAffected` parses to `StatType`, categories
exist, and every hard-coded id the server code depends on exists (grep `Def.Id == "`,
`"warp_zone_`, `"warp_mode_`, boss ids, theme ids, flask ids). Extend it as Phase 3 adds new
references (books → skills, pack pools → enemies, quests → enemies/maps/items). Errors show
as a red banner on the main menu and in the log; the nettest fails on any error.

## Phase 2 — Multiplayer correctness and cost (~45 min)

**2.1 Content fingerprint in the handshake.** Add `GameNetConfig.SimVersion` and a SHA-256
over every `Data/**/*.json` (sorted relative paths, CRLF→LF). Send both in `JoinRequest`;
deny a mismatch with a readable reason ("Game data differs from the host — both players need
the same build"). Bump `ProtocolVersion` to 48. Show the first 8 hex chars of the
fingerprint on the main menu. Nettest both mismatch cases.

**2.2 Coalesce character sync.** Replace immediate `CharacterChanged` calls with a
`CharacterDirty` flag on `ServerPlayer`, flushed once at the end of `GameServer.Update` (same
tick, so inventory actions feel the same). Fold `SkillXpDirty` in, keeping skill-XP-only
changes on their slower 1.1 s cadence. Broadcast `PlayerAppearance` only when
appearance-relevant equipment changed. Nettest: one AoE killing 20 enemies → client
receives ≤2 `CharacterState` packets (count them in `GameClient`).

---

## Phase 3 — Content (the rest of the night, in this order)

Design targets: new content serves **levels 1–15**, extending the playable range past ~10.
Gear stays capped where it is. Each task ends with nettest checks proving the content
spawns, works server-side and replicates to a second client.

### 3.1 Data-driven enemy pools per theme (~45 min)

Pack compositions are hard-coded in `SetupForest` and the survival/defense rollers, so every
theme gets the same undead. Move them into data:

- `themes.json` gains optional `PackPool`: a list of
  `{ "Entries": [["enemy_id", count], ...], "Weight": n, "MinLoop": n, "MinLevel": n }`,
  plus `BossPack` (`{ "Boss": "id", "Guards": [["id", n]], "MinLevel": n }`) and
  `WaveEnemies` (weighted ids for survival waves).
- `SetupForest`, portal-run generation and survival waves use the map theme's pool. A theme
  without a pool falls back to today's hard-coded lists, so graveyard/tomb behave exactly
  as before. Move today's lists into the `graveyard` and `tomb` entries.
- Keep the **test-grounds campaign loop** and the **story road** on their current undead
  compositions, so the existing checks stay meaningful.
- Pack leaders and elite affixes keep working for any enemy id. Audit code that assumes
  undead (buried rising, `AllUndead`, Grave Caller raises) so it stays undead-only.
- Validator: every pool id exists.

### 3.2 Bandits and goblins for the forest (~1.5 h)

Zombies stay in the grave/swamp theme. The forest (Mirewood) gets two new families, mixed
through its `PackPool`. Use existing behavior fields (`AttackStyle` lunge/sword, `Ranged`,
`KeepDistance`, dash fields, cast fields, `SpriteScale`) — no new AI systems tonight.
Neither family is `Undead`, and both bleed red.

- **Goblins** (swarmy, weak, fast; packs of 4–6): Goblin Skulker (fast melee lunge, low
  life), Goblin Slinger (ranged, keeps distance, light physical), Goblin Shaman (caster
  using the existing cast-AoE path, fire-themed, rare in packs).
- **Bandits** (fewer, tougher, smarter; packs of 2–4): Bandit Cutthroat (sword-style melee,
  bleeds via a high-damage telegraphed swing), Bandit Archer (ranged, keeps distance),
  Bandit Brute (slow, high life, heavy lunge with big knockback), **Bandit Captain**
  (pack leader / forest boss: existing dash-charge telegraph + guards).
- Native levels 3–8; tune XP/life/damage against the existing undead at the same level.
- Sprites: bandits are humanoids, so derive them from the mercenary/merchant humanoid
  drawing code (`DrawMercenary`) with leather/hood palettes and a weapon per role. Goblins
  get a new `Goblin` `SpriteStyle`: short (~0.75 scale), green skin, big ears, crude weapon,
  two frames like the other styles. Add matching downed/corpse sprites for every new style
  (see the `...Downed` functions).
- The forest boss pack for portal runs becomes the Bandit Captain with bandit guards.
- Nettest: a forest portal run spawns only non-undead from the pool; a graveyard run still
  spawns undead; every new enemy can be spawned, attacks, dies and drops a corpse, on both
  clients.

### 3.3 Skill books and the trainer (~1 h)

The design: players don't get every skill at the start. Rare **skill books** drop from
bosses and specific monsters. Handing a book to the trainer unlocks a few skills; unlocked
skills still cost gold to learn.

- Books are `Curio` items (stackable 1, tradeable, droppable, stashable) with a new
  `UnlocksSkills: [ids]` field on `ItemBase`. Give them a distinct icon and loot-label color.
- `CharacterData.UnlockedSkills` (HashSet of ids). The class start skill is always unlocked.
  Migration: every skill a saved character already knows counts as unlocked.
- **Unlocks belong only to the player who turns the book in.** Turn-in mirrors the
  researcher's blueprint hand-in: new `SkillBookTurnInRequest(itemInstanceId)`, server checks
  the trainer is within reach (sanctum and ruins hub), consumes the book, adds the skills,
  and messages the player with what was unlocked. A book for skills that are all already
  unlocked is refused with a message and kept.
- `LearnSkill` (campaign/story) refuses skills not in `UnlockedSkills`. `TrainerUI` lists
  unlocked-but-unlearned skills to buy, shows which books are in the bag with a
  "Hand over" button, and shows locked skills greyed with "Requires: <book name>". Test
  grounds (`ARPG_ARENA`) keep everything unlocked.
- Books (2–3 skills each, themed). Example set, adjust to the final skill list: *Hammer and
  Anvil* (mace skills), *Fletcher's Manual* (bow skills), *Grimoire of Embers* (fire bolt +
  fire stuff), *Rime Codex* (ice spike + cold), *Storm Letters* (chain lightning + arcane
  burst), *Ossuary Rites* (both summons), *Shieldbearer's Drill* (shield bash + a mace
  skill).
- Drops: a new per-enemy `ExtraDrops: [{ "ItemId": id, "Chance": p }]` field on enemy defs,
  plus a weighted `SkillBookDropChance` / book list on the boss loot table. Bandits drop
  bow and shield books rarely, goblin shamans drop spell books rarely, bosses drop any book
  at a higher rate.
- **Onboarding guarantee:** the first time a character is present for a Barrow Lord kill
  and the first Codex kill, one book drops **per present player who hasn't received that
  guarantee yet** (flag on `CharacterData`). Each player picks up their own. Prefer books
  matching that player's class start skill. Set the flag when the book is **picked up**,
  not when it drops: a book left on the ground is gone when the map changes, and the player
  gets the guarantee again the next time they kill that boss.
- Nettest: turn-in consumes the book and unlocks for the turner only (the second client
  unchanged); learning a locked skill fails; migration keeps old skills; first-kill
  guarantee drops one per eligible player and none on a repeat kill after pickup; a book
  left on the ground drops again on the next kill.

### 3.4 Mace identity and new mace skills (~1.5 h)

Maces have three skills and their modifiers add no variety. Give the weapon a mechanical
identity built on what already exists (knockback, stun buildup, slams, tremors, fissures):

- **Crash (mace-wide mechanic):** when a mace skill's knockback drives an enemy into a wall,
  structure or another enemy, the target takes bonus damage (a % of the hit) and gets a
  burst of stun buildup. An enemy it was slammed into takes a smaller share. Show it with a
  dust puff and a "Crash" damage tint. All numbers in JSON.
- **Leap Slam:** leap to the aimed point (reuse the shield-bash lunge prediction and
  i-frames), AoE impact on landing that knocks enemies outward (so Crash triggers near
  walls). Mace mobility.
- **Skull Crack:** slow single-target blow with huge stun buildup. Against stunned or frozen
  targets it deals +100% damage and extends the stun. The payoff for the stun game.
- **Fault Line:** a slam that sends a fissure forward along the aim, erupting in three
  delayed bursts (reuse the tremor/fissure systems and visuals).
- **Mace-only modifiers** (prefix/suffix, tiers on the existing ilvl bands, maces only):
  *Concussive* (+% stun buildup), *of Impact* (+% knockback and Crash damage), *Seismic*
  (+% area for Slam skills), *Crushing* (+% damage vs stunned).
- **Skill scroll *Aftershock*** (`Slam` tag): the slam repeats at 50% damage 0.6 s later.
- Put the new skills into books from 3.3. Nettest each skill's server effect, Crash
  against a wall vs. open ground, and each modifier actually changing its number.

### 3.5 Bow skills (~1 h)

Bows have Arrow Shot, Arrow Rain and Piercing Shot. Lean on what bows can own: charging,
range and mobility.

- **Snipe:** `Chargeable` (the shield-bash charge system). Hold to charge; release fires
  one arrow whose damage, range and speed grow with charge; at full charge it pierces
  everything in line.
- **Split Arrow:** a fan of 5 arrows in a cone; brutal at point blank when several hit one
  target, weak at range.
- **Disengage:** a backward hop away from the aim (dodge system, brief i-frames) that fires
  a slowing arrow toward the aim.
- **Bow-only modifiers:** *Piercing* (+1 pierce), *of the Hawk* (+% projectile speed and
  range), *Barbed* (bleed chance with bow attacks).
- Put them into *Fletcher's Manual* and a second bow book. Nettest each skill.

### 3.6 A fourth portal-run zone (~45 min)

Portal runs only roll three zones (`warp_zone_*` in `ServerScrollRuns.cs` →
forest/graveyard/tomb). The `arid` theme ("Scorched Bluffs") already exists and nothing uses
it. Add `warp_zone_arid` → the arid theme, named something like "the Scorched Bluffs", with
its own `PackPool`: a bandit stronghold. Bandit-heavy packs, more archers on high ground,
the Bandit Captain as boss. Move the zone mapping from the hard-coded switch into data
(the modifier def or theme entry carries the theme id and display name). Check the arid
props and ground read well on the run-map generator (use the dev screenshot harness if
xvfb is available, otherwise review the theme fields against forest). Nettest: an arid
warp scroll opens an arid run populated from its pool.

### 3.7 Quest tracker (~1.5 h)

**Sync model (decided by the owner): every character has their own quest log, and there is
no catch-up.** A character completes an objective only by being present when it happens.

- `Data/Quests/quests.json`: quests with ordered steps. Step types: `reach_map` (map kind or
  zone), `kill` (enemy id, count, optional map kind), `kill_boss` (enemy id), `talk` (npc
  id), `turn_in` (item id or category, e.g. any skill book), `use_podium`,
  `complete_run`. Rewards: gold, character XP, optional item id.
- First quest line follows the existing story: reach the camp → fell the Barrow Lord →
  speak to the trainer → descend to the archive and fell the Gilded Codex → place a warp
  scroll on the podium → finish a portal run. Add two side quests for the new content
  (clear goblins in the Mirewood; bring down a Bandit Captain) and one "hand a skill book
  to the trainer" quest.
- Server-authoritative in a new `ServerQuests.cs` partial. Hook the existing events (map
  change, kills, NPC interaction, turn-ins, podium, run complete). When an objective event
  fires, **every player on that map** (alive or dead) who has that step active gets
  credit. Kill counts are per player. Turn-ins credit only the player who turned in.
  Quest items are never auto-added: a step that needs an item completes when **that
  player picks it up**, and an item left behind when the map changes is lost. The player
  redoes the run or area, so the source must be there again (bosses and guaranteed quest
  drops reappear on the next visit while the step is incomplete).
- Stored in `CharacterData.Quests`, so it saves and rides `CharacterState` (now coalesced).
- **No catch-up:** the world still follows the host (that's how the shared map works), but a
  joiner's log and saved story record change only from what they're present for. Today
  `AdoptStoryProgress` writes the host's story flags into a joiner's saved `StoryProgress`;
  change it so the joiner's saved flags only record things that happen while they're there.
  The host's world state is untouched. Record this behavior change in the report.
- Tracker HUD: a compact panel under the minimap/top right with the active quest's current
  step and progress ("Goblins slain 7/12"), plus a toggleable quest log panel (default key
  `J`, rebindable). If a party member is on a step you've already done, show a small
  "helping <name>" line. A small broadcast of each player's current quest step is enough.
- Nettest: two clients with different logs on one map: a kill credits both only when both
  have the step; a joiner who wasn't present for the Codex kill doesn't get the
  quest or the saved `CodexFelled` flag; logs survive save/load; turn-in credits only the
  turner.

---

## Phase 4 — Stretch, only with time left before Phase 5

**4.1 Soft-lock audit.** Check and nettest: solo death on each map kind; whole-party wipe
in a portal run; is the first warp scroll guaranteed? (if not, guarantee one from the
Codex); selling your only weapon; **leaving a map without picking up a quest item or
guaranteed book**. Items are never auto-added, and an item left behind is meant to be lost,
so the player redoes that run or area. Make sure that is always possible: the quest step
stays incomplete, the source (boss, chest, guaranteed drop) is there again on the next
visit or run, and nothing is marked done until the item is in the player's bag. A player
disconnecting while others are readied at a door or mid-cutscene; a joiner arriving
mid-cutscene, mid-defense-wave or while the exit is sealed.

## Phase 5 — Handoff (last 45 min, mandatory)

1. **CLAUDE.md** at the repo root, under 150 lines: what the game is, folder map, the
   authority model, invariants (requests not results; deterministic map gen, bump
   `SimVersion` on generation/collision/sim changes; bump `ProtocolVersion` on wire
   changes; numbers live in JSON; no `string.GetHashCode` for synced values; a nettest
   check per feature; use `CharacterDirty`, not `CharacterChanged` directly; enemy packs
   come from theme `PackPool`s; skills unlock via books; quests are per-character with
   presence credit), commands (`--nettest`, `--nettest story`,
   `ARPG_DEVUI`, `ARPG_STORY`), and "README is long — read sections as needed".
2. Update the README: fix stale "Known limitations"/"Recommended next steps", document
   logs/crash files, autosave, the fingerprint, skill books, the new enemies, skills,
   modifiers, zone and the quest tracker. Add new `ARPG_DEVUI` hooks if you made any (a
   `books` hook that drops one of each book and a `bandits`/`goblins` spawner would help
   manual testing).
3. **MORNING_REPORT.md** final pass: summary paragraph; table of commits (hash, task,
   status); nettest count and time before → after; every new enemy/skill/modifier with its
   numbers; behavior changes to existing checks; what was skipped, reverted or not reached
   and why; and **"Play this first"**: exact steps for a 30-minute manual session (story
   mode, which class, where to find the new enemies, how to get a first book, what to try
   with the new mace/bow skills, what still looks rough), plus how to test co-op on the new
   build (both players must show the same fingerprint).
4. Final build + nettest green; push the branch.

---

## After tonight (don't do these now)

A zone/act graph replacing the magic `MapIndex` ints; splitting `HeadlessNetTest.cs` into
per-feature files; composable data-driven enemy abilities so new families don't need code;
gear tiers past 35 once content reaches those levels; server-side movement speed checks if
the game is ever played with strangers.
