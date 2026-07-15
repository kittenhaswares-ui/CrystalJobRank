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
4. The rating engine replays Casual and Ranked wins/losses, independently for
   each job and local rating epoch.
5. If the user explicitly opts in, the plugin submits only the local player's
   match fields to the configured HTTPS API.
6. The server validates the event and recomputes rating itself.

The plugin never uploads other players' names, worlds, content IDs, account IDs,
or scoreboard rows.

Local aggregation also derives persistent per-job personal records and
role-specific streak progress from the same ordered match history. These values
are never part of the leaderboard submission contract.

## Rating model

This is an Elo-like estimate against a fixed 1500 reference pool:

`expected = 1 / (1 + 10 ^ ((1500 - rating) / 2000))`

`delta = round(K * (result - expected))`

- result is `1` for a win and `0` for a loss;
- `K = 64` for the first 10 provisional matches;
- `K = 32` afterwards;
- rating is clamped to `0..3000`;
- every job starts at 1500 and has independent history;
- Casual and Ranked matches affect the same rating and can be uploaded after
  the user opts in;
- Custom and Unknown-queue matches remain available only as local statistics;
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

## Lifetime records and achievements

Every job stores a monotonic all-time rating peak with a 1500 floor plus local
maxima for kills, damage dealt, damage taken, and HP restored. Rating peaks only
use Casual/Ranked rating events; personal scoreboard maxima may use any locally
recorded CC queue. Implausible scoreboard values are rejected for new records
and ignored during history backfill.

Achievements use chronologically ordered Casual/Ranked matches and are isolated
by role. Tank, DPS, and Healer each track current and best Win Streak and
Deathless Streak independently. A match played on another role is absent from
that role's sequence. Custom and Unknown queues neither advance nor break one.
Deathless milestones are `1/3/5/10/20`; win milestones are `3/5/10/20`.

## Local rating resets

The plugin stores an integer rating epoch for every played job. Resetting a job
increments only that job's epoch, so subsequent Casual and Ranked results replay
from 1500 while old matches and scoreboards remain visible. Epochs avoid clock
and late-arrival problems that a timestamp cutoff would introduce.

Local resets never remove community leaderboard results. Allowing users to
delete only their losing public history would make the shared ladder
meaningless; public resets should happen only through an administrator-defined
season transition.

Schema version 3 is the one deliberate exception: upgrading from an older
plugin version increments every local job epoch exactly once, preserving all
matches and lifetime progress. Fresh installations start directly at schema 3,
so the migration cannot run twice. The optional server performs the matching
one-time transition to season 1; old submissions remain stored as season 0 and
are excluded from the current leaderboard.

## Job icon rendering

The UI requests official job textures from the local game through Dalamud's
`ITextureProvider` and the conventional `62000 + ClassJob row ID` icon lookup.
No Square Enix texture is bundled. The icon is tinted with the current rank
metal and surrounded by code-rendered job motifs and increasingly elaborate
rank frames. Missing or not-yet-loaded textures fall back to the job
abbreviation for that frame.

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

The reference community API is `src/CrystalJobRank.Worker`. It runs on a
Cloudflare Worker and materializes ratings in D1. Match insertion,
de-duplication, and a deterministic replay of the affected player/job stream
share one D1 transactional batch, so parallel and out-of-order uploads cannot
leave a match without its corresponding rating state.

The hosted reference deployment uses
`https://crystal-job-rank-api.kittenhaswares.workers.dev` as its API base URL.
Plugin configuration version 2 makes this the default and migrates only the
exact former placeholder URL; custom deployments and existing account settings
remain unchanged.

The production D1 database is created with the EU jurisdiction setting. The
Worker stores only the chosen alias, authentication hash, rating state, and the
minimum match fields needed for ordering and de-duplication. Territory,
duration, and raw scoreboard values are validated in transit but discarded; a
SHA-256 digest of the complete validated submission is retained to distinguish
an exact retry from conflicting data.

The plugin persists failed uploads as a bounded, identity-bound FIFO of local
match IDs. It sends strictly from the head, uses capped exponential backoff,
and reconstructs each submission from local history, so the retry file contains
neither API keys nor duplicated scoreboard data. The Worker caps late-event
replay depth, aggregate daily registrations, and per-job match volume with D1
triggers; edge rate limits are not the authoritative protection.

`src/CrystalJobRank.Server` remains a dependency-free ASP.NET development and
self-hosting alternative. Its JSON store is safe for one process and a small
MVP, but it is not the storage used by the hosted reference leaderboard and
must not be scaled to multiple replicas.

Public deployment requirements:

- expose only the final HTTPS Worker hostname to plugins and never follow
  authentication redirects;
- keep Worker observability free of request bodies, aliases, fingerprints,
  addresses, and API keys;
- apply reviewed D1 migrations before deploying matching Worker code;
- retain database uniqueness constraints even when edge rate limits are used;
- publish retention and deletion behavior, including D1 Time Travel recovery
  versions; and
- do not log `X-Api-Key` headers or request bodies.

The plugin accepts plain HTTP only for loopback development URLs.
The complete reference-service disclosure is in `PRIVACY.md`.

## Patch resilience

The result hook and memory offsets are version-sensitive. They compile against
Dalamud API 15 and were cross-checked for Patch 7.5, but must be tested in game
after every FFXIV patch. Invalid result values, durations, jobs, and scoreboard
ranges are rejected instead of being persisted.
