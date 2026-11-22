# Morning report — Batch 85 overnight

_Running log; final pass at the end._

## Baseline (Phase 0)
- Started: 2026-11-22 08:51 UTC.
- `dotnet build`: 0 warnings, 0 errors. `--nettest`: **841/841**, wall time ~65 s.

## Decisions log
- **Branch:** the plan asks for `batch-85-overnight`, but this cloud session is only allowed
  to push to `ccr-06103ad7-ual5x5`. All work is on that branch (off the default branch, never
  main). Rename/merge it as you see fit.
- .NET 8 SDK wasn't in the container; installed `dotnet-sdk-8.0` from apt (8.0.134).
- **Baseline is flaky.** I ran the untouched baseline suite repeatedly: 3 of 6 baseline
  runs failed 1-2 *old* checks that depend on dice (`Mace Slam slows survivors` is a 60%
  roll, `overlook spitter fires a descending shot`, `five waves beaten: the Last Stand is won`,
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

## Behavior changes to existing checks
- `tiered modifier database loaded (482 modifiers)`: pinned the exact modifier count.
  Now asserts the original database is still 482 entries once the new mace/bow-only
  groups (`mace_*`, `bow_*`) are set aside.
- `every graveyard enemy is undead ...`: used to assert that EVERY non-tome enemy in the
  data is undead. Now asserts the four graveyard sprite families are undead, and that
  bandits/goblins are not, bleed, and never spawn buried.
- `room three: the Gravelord waits ...` / `the Gravelord falls and the last room's exit
  leads home` (story, the Codex's Mirewood scroll): the Mirewood's boss is now the
  Bandit Captain; both checks look for `bandit_captain` (and that no Gravelord is there).

## Progress log
| Task | Status | Checks (full suite) |
|---|---|---|
| Phase 0 baseline | done | 841 |
| 1.1 logging, 1.2 crash handlers, 1.3 tick guard, 1.4 atomic saves, 1.5 autosave, 1.6 join sanitization, 1.7 data validator | done | |
| 2.1 fingerprint handshake (protocol 48, SimVersion 1), 2.2 coalesced character sync | done | 897 |
| 3.1 data-driven enemy pools per theme | done | 906 |
| 3.2 bandits and goblins (7 enemies, sprites, shove, bleed), snapshot chunking | done | 933 |
| 3.3 skill books and the trainer | done | 959 |
| 3.4 mace identity (Crash, Leap Slam, Skull Crack, Fault Line, 4 modifiers, Aftershock) | done | 985 |
