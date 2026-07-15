# Privacy notice

Last updated: 15 July 2026

Crystal Job Rank works fully locally without the community leaderboard.

## Joining and sharing

Joining is optional and off by default. When you choose to join, the plugin
uses the full name and Home World of the character currently logged in to FFXIV.
There is no editable public-name field.

Joining does not upload old match history. Only your own future Casual and
Ranked results can be sent after sharing is enabled. Turning sharing off stops
new uploads. You can also delete your online identity and its submitted data
from the plugin.

## What stays on your PC

Your full match history, including the post-match scoreboard, local ratings,
records, streaks, and achievements stays in the Dalamud configuration folder.
Other players' names, worlds, IDs, and statistics are never submitted.

## What the leaderboard receives

Registration sends your character name, Home World ID, and Home World name.
Eligible match submissions contain a random match fingerprint, completion time,
job, win or loss, queue type, and only your own post-match statistics. They do
not contain a Square Enix login, account ID, character content ID, hardware ID,
or any other player's data.

The service stores a random player ID, your public character and Home World,
a hash of the API key, submitted match facts needed for ordering and duplicate
protection, and the calculated per-job totals. The public per-job leaderboard
shows character name, Home World, rank, rating, matches, wins, losses, and win
rate. Public entries may be copied by other people or services.

## Hosting and limits

The reference API runs on Cloudflare Workers with an EU-jurisdiction D1
database. Requests use HTTPS. The application does not store raw IP addresses
or intentionally log request bodies or API keys; Cloudflare may process normal
connection and security information under its own policies.

This is an unofficial, community-reported leaderboard. It is not Square Enix
MMR or verified tournament data. Because there is no official match attestation,
a modified client can submit false results.

For questions or deletion problems, open an issue at
https://github.com/kittenhaswares-ui/CrystalJobRank/issues. Never post an API
key or other secret in a public issue.
