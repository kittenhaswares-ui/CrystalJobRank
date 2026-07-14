# Crystal Job Rank

Repository: https://github.com/kittenhaswares-ui/CrystalJobRank

Crystal Job Rank is an experimental Dalamud plugin for **post-match**
Crystalline Conflict statistics. It records the result screen, keeps a local
match history, and calculates a separate community-style rating for every job
you play.

The rating is not Square Enix's hidden matchmaking rating and it is not an
official competitive ladder. It is a transparent, outcome-only estimate:

- every job starts at 1500;
- wins add rating and losses remove rating;
- the first 10 matches use a larger provisional adjustment;
- damage, kills, and healing are displayed but never influence rating.

This repository also contains an optional leaderboard API. A shared leaderboard
cannot work from a static GitHub repository alone: it needs a common service
that receives opt-in match submissions. The included backend can be self-hosted
and stores only a chosen display name, the user's job, result, and the local
player's scoreboard values. Other players' names and IDs never leave the PC.

## Status

Early MVP. The domain model and server can be tested without FFXIV. The Dalamud
capture hook must be verified in game after every FFXIV patch because its
signature and packet layout can change.

## Repository layout

- `src/CrystalJobRank.Core` — deterministic rating engine and shared contracts.
- `src/CrystalJobRank.Plugin` — Dalamud API 15 plugin.
- `src/CrystalJobRank.Server` — optional ASP.NET Core leaderboard API.
- `tests/CrystalJobRank.Core.SelfTest` — dependency-free rating and persistence checks.
- `docs/ARCHITECTURE.md` — privacy, trust, and deployment decisions.

## Build

Requirements: .NET 10 SDK. Building the plugin also restores
`Dalamud.NET.Sdk/15.0.0`.

```powershell
dotnet build CrystalJobRank.slnx -c Release
dotnet run --project tests/CrystalJobRank.Core.SelfTest -c Release
```

Run the development server:

```powershell
dotnet run --project src/CrystalJobRank.Server
```

The API listens on the URL printed by ASP.NET Core. Configure that HTTPS URL in
the plugin before opting in to leaderboard sharing.

## Dalamud development install

Build `src/CrystalJobRank.Plugin`, add the resulting
`CrystalJobRank.Plugin.dll` as a development plugin in Dalamud, then use
`/cjr`. The plugin records only the post-match results payload; it does not
render live combat information or automate gameplay.

## Distribution caveat

Dalamud's official repository currently rejects PvP plugins that could create a
competitive advantage. This project intentionally avoids live assistance, but
official acceptance is still unlikely and must be discussed with the Plugin
Approval Committee before submission. A custom repository is the realistic
distribution path.

## License and attribution

Crystal Job Rank is released under the MIT License. Research for the current
post-match result layout was cross-checked against the MIT-licensed PvP Tracker
project. See `THIRD_PARTY_NOTICES.md`.
