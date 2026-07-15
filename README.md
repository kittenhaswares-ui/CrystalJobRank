# Crystal Job Rank

Crystal Job Rank is an experimental Dalamud plugin for post-match
**Crystalline Conflict** statistics. It records result screens, keeps local
match history and records, and calculates a separate seasonal rating for every
character and job.

Repository: https://github.com/kittenhaswares-ui/CrystalJobRank

## Version 0.6

The community leaderboard is automatic. There is no registration form,
editable alias, sharing checkbox, account-wide rating, or character lock.
After an eligible result screen appears, the plugin reads the local player's
character name, numeric Home World ID, job, and result from that result and
queues it for the shared leaderboard. The service derives the public Home
World name from the numeric ID instead of trusting an editable label.

The public rating key is:

`character name + numeric Home World ID + job + community season`

This means:

- every character is independent, even on the same installation;
- every job for that character is independent;
- the same character merges into one profile when played from another PC;
- changing character switches profiles automatically after that character's
  first captured result;
- no freely editable alias can appear on the board; and
- public rows are shown as `Character Name · Home World`.

The board combines players across regions. Region and data center are not
uploaded or used as filters.

Version 0.6 deliberately starts a fresh local rating generation and a fresh
community season once. Old registration credentials and pending uploads are
discarded. Match history, scoreboard records, and unlocked achievement badges
remain local and are preserved.

## Rating model

FFXIV does not expose a usable opponent MMR for this plugin, and normal
matchmaking is not treated as an MMR ladder. The rating therefore uses only a
job's seasonal wins and losses:

`rating = 1500 + 1000 × (wins − losses) / (wins + losses + 40)`

The `+40` is a symmetric 20-win/20-loss prior. It keeps a tiny sample from
jumping straight to an extreme while allowing a sustained record to move the
rating naturally. The displayed value is rounded deterministically; damage,
kills, deaths, healing, assists, and crystal time never affect it.

Examples:

| Record | Rating |
| --- | ---: |
| 0–0 | 1500 |
| 1–0 | 1524 |
| 6–4 | 1540 |
| 9–1 | 1660 |
| 10–0 | 1700 |
| 5–5 | 1500 |
| 0–10 | 1300 |

Rules:

- Casual and Ranked wins and losses count equally.
- A normal Casual match counts equally whether queued solo or as an allowed
  two-player group/premade; there is no group penalty.
- Custom and unknown-queue matches remain local and never affect or enter the
  community rating.
- The first result immediately creates a visible provisional entry.
- An entry receives a numbered leaderboard position after 10 matches.
- Results are order-independent, so delayed retries cannot change the final
  value.

The visual tiers are Bronze below 1600, Silver at 1600, Gold at 1700,
Platinum at 1800, Diamond at 1900, and Crystal at 2000. These are Crystal Job
Rank's presentation tiers, not Square Enix's official CC rank or hidden MMR.

## Local records and achievements

The job cards use official in-game job icons loaded from the local FFXIV
installation. Rank-metal colors, frames, and job-specific ornaments become
richer at higher tiers. The plugin does not redistribute Square Enix textures.

Each job also tracks personal best kills, damage dealt, damage taken, healing,
and its rating peak for the current rating rules. The header keeps role badges
for Tank, DPS, and Healer:

- **Flawless** — finish 1, 3, 5, 10, or 20 eligible matches in a row without
  dying;
- **Win Streak** — win 3, 5, 10, or 20 eligible matches in a row.

Job changes inside the same role continue that role's sequence. Custom and
unknown matches neither advance nor break it. The highest unlocked badge stays
unlocked after an active streak ends.

![Job rank upgrade art-direction board](assets/concepts/job-rank-upgrades.png)

The concept board was generated for art direction. The shipping UI uses the
real job icon from the game and code-rendered ornaments.

## Community service and privacy

A cross-PC leaderboard needs a shared backend; a static GitHub repository
alone cannot receive match results. The reference service runs on Cloudflare
Workers with a D1 database:

`https://crystal-job-rank-api.kittenhaswares.workers.dev`

Only the local player's eligible future results are sent. Other players'
names, IDs, worlds, and scoreboard rows are never submitted. The random local
installation secret is used only for transient request rate limiting and is
not stored or linked to a character by the service. Public rows show character
name, Home World, job rating, matches, wins, losses, and win rate. The short
disclosure is in [`PRIVACY.md`](PRIVACY.md).

The service is community-reported, not cheat-proof. A modified client can
fabricate results because Square Enix provides no match-attestation API. It
must never be presented as an official competitive ranking.

## Install

Add this URL under **Dalamud Settings → Experimental → Custom Plugin
Repositories**:

```text
https://raw.githubusercontent.com/kittenhaswares-ui/CrystalJobRank/main/repo.json
```

Save, open `/xlplugins`, search for **Crystal Job Rank**, and install it. Use
`/cjr` to open the window.

Local rating reset commands remain available for testing and private views:

```text
/cjr reset SGE
/cjr reset DRK
/cjr reset all
```

Job abbreviations are case-insensitive official three-letter codes. A local
reset does not erase or reset the community-season rating.

## Build and test

Requirements: .NET 10 SDK. Building the plugin restores
`Dalamud.NET.Sdk/15.0.0`. The Worker uses pnpm 11.

```powershell
dotnet build CrystalJobRank.slnx -c Release
dotnet run --project tests/CrystalJobRank.Core.SelfTest -c Release

cd src/CrystalJobRank.Worker
pnpm install
pnpm check
pnpm test
pnpm exec wrangler deploy --dry-run
```

Repository layout:

- `src/CrystalJobRank.Core` — deterministic rating engine and shared models;
- `src/CrystalJobRank.Plugin` — Dalamud API 15 capture and UI;
- `src/CrystalJobRank.Worker` — hosted Cloudflare Worker/D1 API;
- `src/CrystalJobRank.Server` — local ASP.NET development API;
- `tests/CrystalJobRank.Core.SelfTest` — dependency-free behavior checks;
- `docs/ARCHITECTURE.md` — data flow, trust model, and deployment notes.

The result hook and memory layout are patch-sensitive and must be verified in
game after FFXIV updates. The plugin only reads the post-match result payload;
it does not render live combat assistance or automate gameplay.

## Distribution caveat

Dalamud's official repository has historically restricted PvP plugins that
could create a competitive advantage. Crystal Job Rank intentionally operates
only after a match, but a custom repository remains the realistic distribution
path.

## License and attribution

Crystal Job Rank is MIT licensed. Post-match interoperability research was
cross-checked against the MIT-licensed PvP Tracker project. See
[`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md).
