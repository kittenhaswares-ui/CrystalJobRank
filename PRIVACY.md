# Privacy

Last updated: 15 July 2026

## Scope

Crystal Job Rank consists of a local Dalamud plugin and an optional community leaderboard. The plugin can be used locally without creating a leaderboard identity or sharing match results.

This notice describes the reference leaderboard configured by the official Crystal Job Rank release at `https://crystal-job-rank-api.kittenhaswares.workers.dev`. If you enter a different server URL, your data is sent to that server's operator and this notice may not apply.

Crystal Job Rank is not operated or endorsed by Square Enix, Dalamud, or Cloudflare.

## Your choice

Community sharing is off by default. Creating a leaderboard identity does not upload existing match history. Future match uploads begin only after you separately enable sharing in the plugin.

Turning sharing off pauses new and queued uploads but does not delete data already submitted. Re-enabling sharing resumes the queue in order. Deleting the online account clears its local upload queue.

## Data kept on your PC

The plugin stores its local match history in its Dalamud configuration directory. This can include:

- match time, arena, duration, result, queue, and crystal progress;
- the post-match scoreboard, including player names, worlds, jobs, teams, and statistics;
- local ratings, records, streaks, and achievements; and
- leaderboard settings, the chosen public display name, random player ID, and API key.

When sharing is enabled, a separate bounded retry file can store the random IDs of pending local matches, retry counts and times, and the player/server binding. It does not duplicate submissions, scoreboard rows, or the API key; upload data is reconstructed from local match history only when a retry runs.

Other players' scoreboard data remains local and is not included in leaderboard submissions.

The API key is stored as plain configuration data on your PC. Treat it as a secret: anyone who obtains it can submit results or delete the corresponding online account. Resetting ratings or deleting the online account does not erase the local match history.

## Data sent to the reference leaderboard

Registration sends the public display name you choose.

When sharing is enabled, each new Casual or Ranked match sends:

- the API key in an HTTPS request header;
- a random match fingerprint;
- match completion time;
- your job, win or loss, and queue;
- territory ID and match duration; and
- only your own kills, deaths, assists, damage dealt, damage taken, healing, and crystal time.

No Square Enix login, FFXIV account ID, character content ID, hardware identifier, or other players' names, worlds, identifiers, or statistics is submitted.

Cloudflare necessarily receives connection information such as the source IP address when serving the request. The Worker uses a transient SHA-256-derived rate-limit key for anonymous requests; the raw address and derived key are not stored in D1 by the application.

## Data stored by the reference leaderboard

Cloudflare D1 stores:

- a random player ID;
- the chosen display name and a normalized lookup form;
- a SHA-256 hash of the API key, not the API key itself;
- registration time;
- match fingerprint and a SHA-256 hash of the complete submitted match;
- completion and receipt times, job, result, queue, and season; and
- calculated rating, match, win, and loss totals.

Territory, duration, and raw scoreboard values are validated when received but are not retained as database columns. They contribute to the stored submission hash used to recognize identical retries and conflicting submissions. Scoreboard performance does not affect rating.

## Public information

Leaderboard responses are public and contain:

- chosen display name;
- job and leaderboard position;
- rating; and
- matches, wins, losses, and win rate.

Use a pseudonym if you do not want your leaderboard entry associated with your FFXIV character. Public responses permit short-lived caching, and third parties may independently copy public information.

## Purposes

The service processes this data to:

- create a pseudonymous leaderboard identity;
- accept and deduplicate eligible match submissions;
- calculate deterministic, job-specific ratings;
- publish community leaderboard totals; and
- protect the service through validation, authentication, and rate limiting.

Data is not sold or used for advertising by the project.

## Hosting and logs

The reference API at `https://crystal-job-rank-api.kittenhaswares.workers.dev` runs on Cloudflare Workers and stores its database in Cloudflare D1. The production D1 database is created with Cloudflare's EU jurisdiction setting. That setting applies to D1 data location; Cloudflare's network may still process requests and connection metadata at edge locations.

Persistent Worker observability is disabled. The application does not intentionally log request bodies, display names, match fingerprints, IP addresses, or API keys. On an unexpected application error, it emits only a generic message and a random request ID. Cloudflare may independently process operational or security information under its own policies.

See [Cloudflare's Privacy Policy](https://www.cloudflare.com/privacypolicy/).

## Retention and deletion

Submitted records currently have no automatic expiry and remain until the leaderboard account is deleted or the service is discontinued.

"Delete leaderboard account" removes the active player record and, through database cascades, its submitted matches and ratings. It does not delete local plugin history.

Under the current Cloudflare D1 Free plan, Time Travel recovery versions may retain deleted database content for up to 7 days. These recovery versions are not used by the active leaderboard. A restore of an older database version could temporarily restore previously deleted records, which would need to be deleted again.

Deletion cannot remove copies previously made by independent third parties.

## Security and trust limits

API keys are random bearer credentials and are stored by the service only as SHA-256 hashes. Requests use HTTPS, validation, rate limits, database constraints, and transactional updates. No internet service can guarantee absolute security.

Leaderboard results are self-reported by plugin clients. The service does not receive an authoritative Square Enix match identifier and cannot prove that a submission came from an untampered game client. Fingerprints prevent ordinary duplicate uploads but are not anti-cheat evidence.

The leaderboard is experimental, unofficial, and must not be presented as Square Enix matchmaking rating, verified tournament data, or proof of player conduct.

## Contact

For privacy questions or deletion problems, open an issue at:

https://github.com/kittenhaswares-ui/CrystalJobRank/issues

GitHub Issues are public. Never post an API key, IP address, or other sensitive information in an issue. Maintainers can arrange a safer channel if verification is required.

## Changes

Material changes to this notice will be published in this repository with an updated date.
