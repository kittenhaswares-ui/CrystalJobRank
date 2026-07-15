# CrystalJobRank Cloudflare backend

This directory contains the community leaderboard API. It runs on a Cloudflare
Worker with D1, so no permanently running server is required.

## Production endpoint

```text
https://crystal-job-rank-api.kittenhaswares.workers.dev
```

Health check: `GET /health`.

## Automatic v2 contract

There is no registration endpoint and no character/account credential. The
plugin generates one random 32-byte installation secret, persists it locally,
and sends its 43-character base64url representation in the
`X-Installation-Key` header. The secret is invisible to the player and never
locks a character to an installation. One installation may submit different
characters, and multiple installations may submit the same character. Its hash
is used only as a transient Cloudflare rate-limit key; neither the secret nor
its hash is persisted in D1 or attached to a match.

`POST /v2/matches` requires `Content-Type: application/json`, the installation
header, and this body:

```json
{
  "matchKey": "64 lowercase hexadecimal characters",
  "completedAtUtc": "2026-07-15T10:00:00.0000000Z",
  "characterName": "Test Dancer",
  "worldId": 21,
  "worldName": "Ravana",
  "job": 17,
  "outcome": 1,
  "queue": 1,
  "territoryId": 1032,
  "durationSeconds": 300,
  "stats": {
    "kills": 3,
    "deaths": 0,
    "assists": 5,
    "damageDealt": 750000,
    "damageTaken": 500000,
    "hpRestored": 25000,
    "timeOnCrystalSeconds": 45
  }
}
```

The first accepted match automatically creates the normalized
`characterName + worldId` identity and its independent job rating. Casual (`1`)
and Ranked (`2`) count equally. Solo, group, and premade matches count; the API
does not apply a party-size condition. Custom and unknown queues are rejected.
The server maps every supported global-service `worldId` to its canonical Home
World name; the submitted `worldName` is compatibility input and is never used
for public data. Unknown World IDs are rejected.

A successful response is:

```json
{
  "job": 17,
  "rating": 1524,
  "matches": 1,
  "wins": 1,
  "losses": 0,
  "winRate": 1,
  "isProvisional": true
}
```

Public rows come from `GET /v1/leaderboard?job=DNC&limit=50`. Players with
fewer than ten matches remain visible with `rank: 0` and
`isProvisional: true`. Established players are numbered first by rating, then
match count and deterministic character/World tie-breakers.

There is deliberately no mid-season reset or delete endpoint.
That prevents players from splitting losses across throwaway installation keys.
A future privacy deletion flow should remove a complete character season and
prevent rejoining until the next season.

## Rules v4 rating

Each character, job, and season has separate win/loss counters. Rating is the
integer result of a symmetric Beta(20,20) prior:

```text
rating = 1500 + roundAwayFromZero(1000 * (wins - losses) / (matches + 40))
```

The formula is order-independent and updates in constant time. Kills, damage,
healing, queue type, and party size never alter the number of points awarded.
Examples: 1-0 = 1524, 6-4 = 1540, 9-1 = 1660, 10-0 = 1700,
5-5 = 1500, and 0-10 = 1300.

The idempotency key is `(season, normalized character identity, matchKey)`.
An exact retry returns HTTP 200 with the current rating. Reusing that key with
different validated data returns HTTP 409. D1 uniqueness is authoritative, and
match insertion plus the rating counter update use one transactional batch.
Matches completed before the active season began are rejected, and a D1 trigger
atomically refuses writes if that season closes or changes during a request.

Migration `0004_automatic_character_ranking.sql` intentionally removes all
registration-era players, matches, and ratings, closes Season 1, and starts
Season 2 with schema/rating-rules version 4. Old data is not carried forward.

## Privacy and abuse controls

- The installation secret and its SHA-256 digest are never persisted. The
  digest exists only transiently while Cloudflare applies a rate limit.
- Public data is limited to character name, Home World name, job, seasonal
  rating, and aggregate record.
- The database retains only the minimum match fields needed for idempotency,
  limits, and counters. Validated scoreboard details are represented only in a
  canonical payload digest and are not stored as raw statistics.
- Connecting IP addresses are transiently hashed for Cloudflare rate-limit
  keys and are not stored in D1 or application logs.
- Invocation logs are disabled. Observability remains enabled only for sampled
  traces and explicit operational failures. Structured
  application errors contain only a random request ID, method, path, and error
  class—never bodies, names, Worlds, match keys, IP addresses, or secrets.
- No CORS headers are emitted. JSON is streamed and aborted above 16 KiB. All
  SQL values are bound parameters, and timestamps/numeric fields are bounded.
- Native rate limits allow 60 write attempts per connecting-address hash,
  15 write attempts per transient installation hash, and 120 reads per minute.
  D1 triggers additionally cap a character/job at 100 matches per UTC day and
  5,000 per season.

## Trust boundary

The API prevents malformed, duplicated, out-of-season, and obviously abusive
submissions, but an open-source game client cannot provide tamper-proof
attestation. A modified client can still invent character names or results.
The community board is therefore an honor-system statistic and needs moderation
if it is exposed beyond a small trusted plugin community. Numeric World IDs and
their public labels are server-validated; character existence is not.

## Deploy and verify

Prerequisites: Node.js 22+, pnpm 11, and Cloudflare Workers/D1.

```text
pnpm install
pnpm exec wrangler login --use-keyring
pnpm exec wrangler d1 create crystal-job-rank --jurisdiction eu
```

The official D1 UUID is already in `wrangler.jsonc`. A fork must replace it
with its own database UUID. Then run:

```text
pnpm check
pnpm test
pnpm exec wrangler deploy --dry-run
pnpm db:migrate:remote
pnpm deploy
```

For local work, use `pnpm db:migrate:local` and `pnpm dev`. The three rate-limit
namespace IDs in `wrangler.jsonc` must remain distinct within a Cloudflare
account. Do not deploy before the migration and Worker version are ready to go
live together.

## Starting another season

Create a new season row and update `app_settings.current_season` in one reviewed
migration. Never reuse a season ID. A rating-formula change must also increment
`rating_rules_version`; a schema change must increment `schema_version`.
