# CrystalJobRank Cloudflare backend

This directory contains the production API for the optional community leaderboard. It runs on a Cloudflare Worker and uses D1; no permanently running server or paid dependency is required.

## Production endpoint

The official Crystal Job Rank release uses:

```text
https://crystal-job-rank-api.kittenhaswares.workers.dev
```

The public health check is
`https://crystal-job-rank-api.kittenhaswares.workers.dev/health`. Plugin users
may configure another HTTPS deployment before creating an identity; that
deployment has its own operator and privacy policy.

## Deploy

Prerequisites: Node.js 22 or newer, pnpm 11, and a Cloudflare account with Workers and D1 enabled.

```text
pnpm install
pnpm exec wrangler login --use-keyring
pnpm exec wrangler d1 create crystal-job-rank --jurisdiction eu
```

The EU jurisdiction must be selected when the database is created and cannot be added later. `wrangler.jsonc` contains the UUID of the official production D1 database, not a placeholder. For a fork or another Cloudflare account, replace that value with the UUID returned by your own `d1 create` command, then run:

```text
pnpm db:migrate:remote
pnpm deploy
```

`pnpm db:migrate:local` and `pnpm dev` provide a local database and development Worker. `pnpm test` runs the Rules-v3 golden tests and the Worker/D1 integration test; `pnpm check` type-checks the project.

The four `namespace_id` values in `wrangler.jsonc` are deliberately separate. If those numbers are already used by another rate-limit binding in the same Cloudflare account, replace them with other positive integers before deployment.

## HTTP contract

- `GET /health`
- `POST /v1/players/register` with `{ "characterName": "...", "worldId": 21, "worldName": "Ravana" }`
- `POST /v1/matches` with `X-Api-Key`
- `GET /v1/leaderboard?job=DRK&limit=50`
- `DELETE /v1/players/me` with `X-Api-Key`

JSON field names and numeric enum values match the existing .NET plugin client. Registration uses the character's official in-game name and Home World; identity uniqueness is the normalized `characterName` plus `worldId`. Public leaderboard rows expose `characterName` and `worldName`. Only Casual (`1`) and Ranked (`2`) matches are rated. The server starts each job at 1500 and applies Rules v3 using the canonical `completedAtUtc`, then fingerprint ordering. Scoreboard performance never changes rating.

Schema migration 0003 intentionally invalidates legacy alias-only accounts and their dependent matches/ratings because no trustworthy Home World can be inferred. Registration counters are retained. Users register again with their official character identity after the migration.

An identical retry is idempotent and returns the current rating with HTTP 200. Reusing a fingerprint for different data returns HTTP 409. The D1 unique constraint is the final duplicate authority. Match insertion and rating materialization run in one transactional batch: a chronologically newest match advances the cached rating in constant time, while a genuinely late match deterministically replays that player's job stream. Late matches are accepted only while that season/player/job history has fewer than 128 existing matches. At 128 and above, new chronologically latest matches and exact retries remain accepted, but another late match returns HTTP 409. A D1 trigger is authoritative even if concurrent requests pass the application preflight together.

## Privacy and abuse controls

- API keys contain 256 random bits and are returned only once. D1 stores only their SHA-256 hashes.
- The database stores the official character name, Home World ID/name, and the minimum match fields required to verify idempotency and calculate rating. Scoreboard, territory, and duration values are validated but not retained as raw fields. A SHA-256 digest of the complete validated submission is retained so an exact retry can be distinguished from conflicting data.
- Account deletion relies on foreign-key cascades and removes the player's matches and ratings.
- Worker observability is disabled by default. Application errors log only a random request ID—never request bodies, character names, Home World metadata, fingerprints, IP addresses, or API keys.
- Native free Workers rate-limit bindings allow 5 registrations, 60 authentication attempts, 15 combined authenticated writes per account, and 120 leaderboard/health reads per minute and key. Authentication is IP-limited before an API-key lookup or request-body read; authenticated writes are then keyed by API-key hash. Anonymous limits use a transient SHA-256 hash of the connecting address; the address is not stored in D1. Successful health responses are cached for 15 seconds under a query-free canonical key; failures are never cached.
- No CORS headers are emitted. The Dalamud client does not need CORS, while browser cross-origin JSON requests and the custom API-key header therefore fail preflight.
- JSON bodies are streamed and aborted as soon as they exceed 16 KiB. All SQL uses bound parameters, timestamps have a 90-day past/10-minute future window, and every numeric field has a strict range.
- D1 triggers cap accepted matches at 100 per UTC day and 5,000 per season for each player/job pair. The daily ceiling returns HTTP 429 with a UTC-day retry delay; the permanent season ceiling returns HTTP 409 so an ordered client outbox can discard that match instead of blocking until a new season. A separate D1 trigger caps the whole deployment at 100 account registrations per UTC day. Its aggregate counter is intentionally retained when an account is deleted, so repeated create/delete cycles cannot reopen capacity. One hundred CC matches already represents roughly eight or more hours of play; the ceilings remain generous while bounding abuse.

Cloudflare's native rate limiter is intentionally permissive and locally consistent. Security does not depend on it: D1 uniqueness, authentication, validation, and transaction boundaries remain authoritative.

## Starting a new season

Create the new season row and update `app_settings.current_season` in one migration. Existing matches remain historical and do not enter the new leaderboard. Never reuse a season ID. Rating rules changes should also update `rating_rules_version` and receive their own reviewed migration.
