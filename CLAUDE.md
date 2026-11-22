# Scrollbound — notes for Claude

Isometric co-op ARPG in plain C# / MonoGame (no engine), ~40k lines, LiteNetLib,
**host-authoritative**. All art is procedural. All content is JSON. Early test build:
tuned for roughly levels 1-15.

README.md is long (1,000+ lines) — read sections as needed, don't load it whole.
BALANCE_CHANGES.md is the running record of numbers; every batch appends a section.

## Commands (run from `ARPG/`)

- `dotnet build` — must stay at 0 warnings.
- `dotnet run -- --nettest` — the whole headless suite (server + real clients over
  loopback, no graphics), ~100 s, exit 0 on success. `--nettest story` = story sections
  only; `--nettest b85` = batch 85 sections only (fast iteration).
- A few OLD checks roll real dice (unseeded server RNG) and fail about one run in three:
  `Mace Slam slows survivors`, `enemies drop gold`, `five waves beaten`, `overlook
  spitter fires`, `warriors close to melee range`, `Shattering bursts 5 ice shards`.
  Rerun before believing one red line there. Never delete a check to get green.
- `dotnet run -- --sp` boots straight into single player. Env: `ARPG_STORY=1` (story
  world), `ARPG_ARENA=1` (old arena), `ARPG_THEME=<id>`, `ARPG_DEVUI=<tokens>` (dev hooks
  — list in README §11). The game runs under `xvfb-run` for screenshots
  (`import -window root`); `ARPG_DEVUI=spritesheet` is the art check for enemy sprites.

## Folder map (`ARPG/`)

- `Server/` — the simulation. `ServerWorld.cs` is big (tick, AI, combat, campaign
  flow); **don't rewrite or split it wholesale** — new systems go in new
  `partial class ServerWorld` files: `ServerQuests`, `ServerSkillBooks`, `ServerMace`,
  `ServerPopulation`, `ServerSanitize`, `ServerSafety`, `ServerScrollRuns`,
  `ServerDefense`, `ServerTutorial`, `ServerCharacterOps` (inventory, shop, debug
  commands). `GameServer.cs` = LiteNetLib host, packet translation, tick guard.
- `Net/` — `Packets.cs` (protocol), `GameClient.cs`, `ClientWorld.cs` (replicated view).
- `Sim/CharacterData.cs` — ONE structure that is save file, join payload and server state.
- `World/GameMap.cs` — seeded, deterministic map generation + collision.
- `Data/` — all content JSON, plus `GameData.cs` (models + loader), `DataValidator.cs`,
  `ContentFingerprint.cs`.
- `Skills/`, `Stats/`, `Items/`, `Inventory/` — rules shared by server and client.
- `Render/` (`SpriteGen.cs` procedural sprites, `WorldRenderer.cs`), `UI/` (screens,
  HUD, panels; `PlayScreen.cs` is the in-game shell and owns client prediction).
- `Persistence/` (`SaveManager`, `Autosaver`), `Util/` (`Json`, `Log`, `CrashReporter`).
- `Testing/HeadlessNetTest.cs` (old suite) and `HeadlessNetTest.Batch85.cs` (one
  server per section via the `Session` helper — copy that style for new checks).

## Authority model

- The server owns enemies, AI, combat, loot, drops, pickups and every character
  mutation. Single player is a local server + a loopback client: one code path.
- The server runs on its own thread at 60 Hz; the render thread never touches server
  state. `GameServer.Update` is wrapped in a tick guard (log, skip, stop after 30
  consecutive failures) — don't add a second thread that touches `ServerWorld`.
- Clients send **requests, never results** ("use skill at X", "pick up drop D", "hand
  over book B"). The server validates reach, cost and ownership, then replicates.
- Movement is the exception: client-predicted (walk, dodge, Shield Bash lunge, Leap
  Slam, Disengage hop) and only sanity-clamped by the server.
- Clients rebuild maps from a seed, so generation must be deterministic.

## Invariants (break one and co-op desyncs or progress is lost)

- Wire format changed (packet added, field added/reordered)? Bump
  `GameNetConfig.ProtocolVersion` (Core/GameSettings.cs) and say why in the comment.
- Map generation, collision or simulation rules changed? Bump `GameNetConfig.SimVersion`.
- Data files are covered by the content fingerprint automatically; mismatched builds
  are refused at the handshake with a readable reason.
- Numbers live in JSON (or `Stats/Defense.cs`, `World/DefenseBalance.cs`,
  `Data/Config/*.json`). Every new enemy, skill and modifier gets a line in
  BALANCE_CHANGES.md.
- Never use `string.GetHashCode()` for anything that must match across processes
  (randomized per process). FNV-1a exists in `ServerCharacterOps.cs`.
- Never call `_events.CharacterChanged` directly. Call `MarkCharacterDirty(player)`;
  the character is flushed once per tick. Skill-XP-only changes set `SkillXpDirty`.
- Unreliable packets must stay under ~1 KB (LiteNetLib throws). Snapshots are chunked
  (`GameServer.SnapshotChunk`).
- Enemy packs come from the map theme's `PackPool` (`Data/Zones/themes.json`) on portal
  runs. The test-grounds loop and the story road use `ServerWorld.BuiltInUndead` on
  purpose. Only `Undead` enemies may spawn buried.
- Skills unlock per character via skill books handed to the trainer
  (`CharacterData.UnlockedSkills`); gated in story worlds only. Every skill must be in
  some book or be a class start skill (a nettest check enforces it).
- Quests are per character with **presence credit and no catch-up**: a step completes
  only for players on the map when it happens; nothing is copied from the host; quest
  items are never auto-added (pickup completes the step; the source reappears).
- `CharacterData` gained a field? Null-coalesce and validate it in
  `ServerSanitize.SanitizeCharacter` (the join payload is client-supplied).
- New data cross-reference? Add it to `DataValidator`. New id the code depends on? Add
  it to the validator's `Required*` lists.
- Saves go through `Json.SaveFile` (atomic, keeps `.bak`); character saves through
  `SaveManager` (one writer per file).
- Every feature gets `--nettest` checks in the existing `Check(cond, "sentence")`
  style. If you deliberately change behaviour an old check asserts, update that check
  in the same commit and say so.
- The headless harness runs sim time ~5x faster than real time, and LiteNetLib flushes
  on a ~15 ms real-time thread: allow ~0.2 s of sim latency per hop, poll for a
  condition instead of pumping an exact duration, and pin weapon damage
  (`p.Stats.WeaponMinDamage = WeaponMaxDamage`) when a check compares damage numbers.

## Conventions

- Branch off the default branch; never commit to main. Commits: `Batch N.M: what changed`.
- Comments explain WHY (the codebase is heavily commented in that voice — keep it).
- Debug commands live in `ServerWorld.DebugCommand`; any peer may run them (intentional
  for now). Dev UI hooks live at the top of `PlayScreen`.
- Visuals: reuse existing effects/sounds (tags like `Slam` drive them); new content
  needs to read clearly, not look polished.
