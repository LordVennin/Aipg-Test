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
- **Baseline is flaky.** I ran the untouched baseline suite repeatedly: roughly one run in
  three fails 1-2 *old* checks that depend on dice (`Mace Slam slows survivors` is a 60%
  roll, `overlook spitter fires a descending shot`, `warriors close to melee range and hit`).
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

## Progress log
| Task | Status | Checks (full suite) |
|---|---|---|
| Phase 0 baseline | done | 841 |
| 1.1 logging, 1.2 crash handlers, 1.3 tick guard, 1.4 atomic saves, 1.5 autosave, 1.6 join sanitization, 1.7 data validator | done | |
| 2.1 fingerprint handshake (protocol 48, SimVersion 1), 2.2 coalesced character sync | done | 897 |
