# Architecture and trust model

## Why a shared leaderboard needs a backend

Local match history and per-job ratings need no server. A leaderboard shared by
different PCs needs one common source of truth that can accept writes and serve
the aggregate view. GitHub Pages and a static custom-plugin repository are
read-only at runtime, so they cannot fill that role.

"Serverless" products such as Cloudflare Workers/D1, Supabase, or Firebase can
reduce operations work, but they are still a backend service in this design.

## Data flow

1. FFXIV produces its normal Crystalline Conflict result payload.
2. The plugin copies that post-match payload after the match has ended.
3. The full scoreboard is stored locally in `matches.json`.
4. The rating engine replays only Ranked wins/losses, independently for each
   job and local rating epoch.
5. If the user explicitly opts in, the plugin submits only the local player's
   match fields to the configured HTTPS API.
6. The server validates the event and recomputes rating itself.

The plugin never uploads other players' names, worlds, content IDs, account IDs,
or scoreboard rows.

## Rating model

This is an Elo-like estimate against a fixed 1500 reference pool:

`expected = 1 / (1 + 10 ^ ((1500 - rating) / 2000))`

`delta = round(K * (result - expected))`

- result is `1` for a win and `0` for a loss;
- `K = 64` for the first 10 provisional matches;
- `K = 32` afterwards;
- rating is clamped to `0..3000`;
- every job starts at 1500 and has independent history;
- only Ranked matches affect rating or get uploaded;
- Casual and Custom matches remain available as local statistics;
- damage, K/D/A, healing, and crystal time never affect rating.

The fixed baseline is deliberate. FFXIV does not expose the community rating of
all opponents, and collecting persistent identities for non-users would create
an unacceptable privacy tradeoff. The number therefore estimates sustained win
rate, not the hidden matchmaking strength of a specific lobby.

The wider logistic scale is calibrated to the visible 100-point divisions:

| Tier | Rating | Estimated win probability vs reference |
| --- | ---: | ---: |
| Bronze | 1500 | 50.0% |
| Silver | 1600 | 52.9% |
| Gold | 1700 | 55.7% |
| Platinum | 1800 | 58.6% |
| Diamond | 1900 | 61.3% |
| Crystal | 2000 | 64.0% |

## Local rating resets

The plugin stores an integer rating epoch for every played job. Resetting a job
increments only that job's epoch, so subsequent Ranked results replay from
1500 while old matches and scoreboards remain visible. Epochs avoid clock and
late-arrival problems that a timestamp cutoff would introduce.

Local resets never remove community leaderboard results. Allowing users to
delete only their losing public history would make the shared ladder
meaningless; public resets should happen only through an administrator-defined
season transition.

## Authentication and abuse

Registration creates a random 256-bit API key. The server stores only its
SHA-256 hash and returns the key once. Match deltas sent by clients are ignored;
the server replays outcomes and de-duplicates fingerprints per account.

This is not cheat-proof. The plugin and protocol are open source, so a modified
client can fabricate wins. No client-side secret or signature can solve that.
Meaningful hardening would require at least one of:

- corroboration from multiple independent users in the same match;
- a trusted match-attestation source;
- moderation plus anomaly detection and account-age/minimum-match rules.

Until then, the UI and documentation call this a community leaderboard, never
an official rank or MMR.

## Backend deployment

The included JSON store is intentionally small and dependency-free. It is safe
for one server process and an MVP community, but not for multiple replicas or a
large ladder. Move `LeaderboardStore` to PostgreSQL, D1, or another transactional
database before scaling horizontally.

Public deployment requirements:

- terminate TLS at Kestrel or a trusted reverse proxy;
- expose only an HTTPS DNS hostname to plugins;
- persist `/data/server-data.json` or the replacement database;
- back up data and publish retention/deletion policy;
- add reverse-proxy rate limits and monitoring;
- do not log `X-Api-Key` headers or request bodies.

The plugin accepts plain HTTP only for loopback development URLs.

## Patch resilience

The result hook and memory offsets are version-sensitive. They compile against
Dalamud API 15 and were cross-checked for Patch 7.5, but must be tested in game
after every FFXIV patch. Invalid result values, durations, jobs, and scoreboard
ranges are rejected instead of being persisted.
