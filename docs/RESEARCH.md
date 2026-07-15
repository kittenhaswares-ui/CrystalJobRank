# CCStats / PvP Tracker research notes

The requested "ccstats" project is the `/ccstats` command and Crystalline
Conflict window of **PvP Tracker**, repository `wrath16/PvpStats`.

Relevant findings from the reviewed source (version 2.7.0.0 at review time):

- Dalamud API 15 / .NET 10 project.
- MIT-licensed repository.
- A match-end hook copies a post-match results packet.
- The packet contains duration, local-relative win/loss, crystal progress, ten
  player rows, K/D/A, damage dealt/taken, HP restored, and crystal time.
- PvP Tracker also implements match timelines, action-effect parsing, player
  alias linking, Frontline, Rival Wings, extensive filtering, and LiteDB.

Crystal Job Rank deliberately takes a much smaller scope:

- Crystalline Conflict only;
- result screen only;
- no action-effect, kill, HoT/DoT, or live-combat hooks;
- no opponent profiling outside the locally stored result screen;
- local JSON persistence with atomic replacement;
- a new job-isolated outcome rating;
- an automatic character/job leaderboard that uploads only the local player's
  eligible result and never another player's row.

Primary references:

- PvP Tracker source: https://github.com/wrath16/PvpStats
- Current Dalamud API 15 notes: https://dalamud.dev/versions/v15/
- Dalamud plugin restrictions: https://dalamud.dev/plugin-publishing/restrictions/
- Dalamud AI usage policy: https://dalamud.dev/plugin-publishing/ai-policy/
- Dalamud technical considerations: https://dalamud.dev/plugin-development/technical-considerations/

The official Dalamud restrictions state that PvP plugins are generally not
accepted when they could confer competitive advantage. Even though this project
is post-match only, a custom repository is the realistic distribution channel
unless the approval team explicitly agrees otherwise.
