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
4. The rating engine replays only wins/losses, independently for each job.
5. If the user explicitly opts in, the plugin submits only the local player's
   match fields to the configured HTTPS API.
6. The server validates the event and recomputes rating itself.

The plugin never uploads other players' names, worlds, content IDs, account IDs,
or scoreboard rows.

## Rating model

This is an Elo-like estimate against a fixed 1500 baseline:

`expected = 1 / (1 + 10 ^ ((1500 - rating) / 400))`

`delta = round(K * (result - expected))`

- result is `1` for a win and `0` for a loss;
- `K = 72` for the first 10 provisional matches;
- `K = 48` afterwards;
- rating is clamped to `0..3000`;
- every job starts at 1500 and has independent history;
- custom matches are recorded locally but never affect rating or get uploaded;
- damage, K/D/A, healing, and crystal time never affect rating.

The fixed baseline is deliberate. FFXIV does not expose the community rating of
all opponents, and collecting persistent identities for non-users would create
an unacceptable privacy tradeoff. The number therefore estimates sustained win
rate, not the hidden matchmaking strength of a specific lobby.

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

