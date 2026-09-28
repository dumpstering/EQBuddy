# AGENTS.md — dumpstering's EQBuddy fork

Cross-tool rules (Codex, other non-Claude agents). Fork of
`DranakCorps-bot/EQBuddy` (`upstream` remote); `origin` is
`dumpstering/EQBuddy`. `CLAUDE.md` at repo root is **upstream-owned** — its
org process (seats, bots, PR/review flow, Helm/Scribe/Fable channels) does
not apply to fork-only work. Do not edit it for fork purposes; add fork
rules here instead.

## The fork-only feature
Derived per-teammate stats computed entirely from the **user's own log** —
no second log file, no teammate's client, nothing synced. Core files:
`src/EQBuddy.Core/{DerivedTeammates,TeammatePerspective,TeammateRoster,
TeammateCombine,DuoStats,DuoCompanion,ManualTeammates,LogWatcher.Duo}.cs`.
Tests: everything matching `*Teammate*` or `*Duo*` under
`tests/EQBuddy.Tests/`.

## Sync budget with upstream
- Keep edits to upstream-owned files **minimal** — small, targeted hooks
  only (e.g. one call site in `LogWatcher`/`SessionStats`).
- New logic goes in **new fork files**, never inline into upstream files.
- **Never relocate an upstream method** to make room for fork code; add
  beside it — relocation turns every future upstream merge into a
  needless conflict.

## Commits and identity
Fork commits are authored as `dumpstering <dumpstering@gmail.com>`.
Upstream's commit-identity guard (`scripts/commit-identity-guard.ps1`,
which flags stray `David Edwards`/bot identities) does not apply to
`dumpstering@gmail.com` commits — that's the fork owner, not a
misattributed agent commit.

## Running tests / gate
```
dotnet test tests/EQBuddy.Tests/EQBuddy.Tests.csproj -c Release
pwsh -NoProfile -File scripts/check.ps1
```

## Never push to upstream
`origin` (this fork) only. Never `git push upstream`.
