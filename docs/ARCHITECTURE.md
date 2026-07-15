# Architecture and trust model

## Why the leaderboard has a backend

Local history and ratings need no server. A leaderboard shared by different
PCs needs one common service that accepts writes and serves an aggregate view.
The custom-plugin repository on GitHub is static and cannot do this itself.

The hosted reference implementation is a Cloudflare Worker backed by D1. The
ASP.NET project is a small local/self-hosted development equivalent.

## Post-match data flow

1. FFXIV creates the normal Crystalline Conflict result payload.
2. The hook copies the payload and identifies its own scoreboard row by
   content ID, with a name/Home-World fallback, before returning control to the
   game.
3. The copied result is handed back to the Dalamud framework thread and then
   admitted to a serial persistence worker; neither the detour nor a frame
   update performs file or network I/O.
4. The plugin stores the complete result locally using an atomic temporary-
   file replacement.
5. Casual and Ranked matches update the local character/job rating. Custom and
   Unknown matches remain local statistics only.
6. An eligible match enters a bounded FIFO outbox automatically. Retries reuse
   the same match key and use capped backoff.
7. The Worker validates the request, derives the canonical character identity,
   de-duplicates the event, and updates W/L counters plus rating atomically.

No registration or editable public identity exists. Identity is captured from
each result, so switching characters cannot send a result under a previously
selected profile.

## Character and installation identity

The public seasonal rating key is:

`normalized character name + numeric Home World ID + job + season`

The readable Home World name is presentation data derived by the service from
the numeric ID; a caller-provided label cannot rename a public World or create
another identity. A name change or Home World transfer creates a new public
identity because there is no safe account or content ID in the upload
contract.

Each plugin installation creates a random 256-bit secret. Requests carry it in
`X-Installation-Key`; the Worker hashes it transiently for request rate limits
and stores neither the key nor its digest in D1. This is an invisible
rate-control credential, not an account:

- it is never displayed as a public player ID;
- it is not derived from hardware or Square Enix data;
- it can submit results for any character used on that installation; and
- multiple installations can contribute to the same character/job identity.

There is intentionally no client-side community reset. Public results remain
until the administrator starts a new season. Local `/cjr reset` commands affect
only private local rating epochs.

## Rating rules v4

The model is a Bayesian-smoothed seasonal win rate with a Beta(20,20) prior:

`exact = 1500 + 1000 × (wins − losses) / (wins + losses + 40)`

The displayed integer uses deterministic nearest rounding with midpoint ties
away from the 1500 baseline. Implementations share test vectors so C#,
TypeScript, and SQLite produce the same number.

- Every character/job starts at 1500.
- Casual and Ranked have identical weight.
- An allowed normal Casual group/premade match counts exactly like solo Casual.
- Custom and Unknown queues do not enter the rating.
- Scoreboard performance never changes rating.
- Results are order-independent and can be updated from W/L counters in O(1).
- Rating is bounded by the formula's 500–2500 range.
- Entries with 1–9 matches are visible as provisional with `rank = 0`.
- Established entries are numbered from 10 matches onward.

This is not Elo, Glicko, Square Enix rank, or opponent-adjusted MMR. The client
does not know a trustworthy strength value for the lobby, so inventing one
would add false precision.

## Local history, records, and achievements

The local JSON history contains result time, arena metadata, the ten scoreboard
rows, progress, queue, character identity, rating fields, and the local
player's statistics. It never leaves the PC as a full document.

Personal maxima cover kills, damage dealt, damage taken, and HP restored.
Role achievements track Win Streak and Flawless sequences for Tank, DPS, and
Healer. The v4 migration starts a clean rating generation once while retaining
local history, non-rating records, and unlocked badge milestones.

Rating epochs make local resets deterministic without deleting history. The
current UI selects the latest captured character and replays only matches for
that character; different characters never share W/L totals.

## Worker storage and consistency

D1 stores only the current season's minimum shared state:

- characters: canonical public character identity and the Home World name
  derived server-side from its numeric World ID;
- matches: season, identity, deterministic match key, payload hash, time, job,
  outcome, and queue; and
- ratings: materialized wins, losses, matches, and formula result.

Territory, duration, and personal scoreboard values are accepted only for
validation and payload conflict detection; raw values are not stored.

One D1 batch inserts the deduplicated match and upserts its counter-based rating
state. The `(season, identity, match key)` primary key makes retries idempotent.
Database constraints enforce counter consistency, supported job/queue values,
rating formula consistency, the active open-season boundary, and
per-character/job volume caps. An offline result completed before the current
season began is rejected instead of being assigned to the new season.

Migration `0004` intentionally ends community season 1, removes registration-
era players/results, creates the automatic character schema, starts season 2,
and switches to rating rules 4. It is applied once before the matching Worker
code is deployed.

## Privacy and abuse limits

The plugin never uploads another player's name, world, content ID, account ID,
or scoreboard row. The Worker must not log request bodies, headers, character
names, match keys, or network addresses. Character name and Home World are
public by design; all other shared fields are used to compute or validate the
community row.

The protocol is not cheat-proof. An open-source client can fabricate a result,
and a client-held secret cannot attest that a game occurred. Stronger integrity
would require an official match source, independent multi-client corroboration,
or moderation/anomaly detection. Until then, the UI calls it a community
leaderboard and never an official rank.

## Patch resilience

The result hook, native signature, and packet offsets are version-sensitive.
They must be checked after each FFXIV patch. Queue classification uses the
current Content Finder condition IDs: normal solo and allowed two-player Casual
registration share the Casual path, while known custom duties remain excluded.
Invalid names, worlds, jobs, results, durations, and scoreboard ranges are
rejected instead of being persisted or uploaded.
