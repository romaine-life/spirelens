# Replay Player

<!-- This branch (claude/run-database) adds only the "Playlists" section
     below. The rest of this document lives on claude/replay-player; when the
     two meet, keep that document and append this section to it. -->

## Playlists

Playlists are built outside the game, on the `spirelens-db serve` web page
([run-database.md](run-database.md), "Run browser and playlists"). The page
and the game share files in `%APPDATA%\SlayTheSpire2\SpireLens\playlists\`
(`user://SpireLens/playlists/`). The full contract, with examples, is in
run-database.md under "Playlist files". In short:

- **`<slug>.json`** is one saved playlist:
  `{"format_version":1,"name":…,"created_at_utc":…,"updated_at_utc":…,"filter":{…}|null,"runs":[{"game_start_time":…,"label":…}]}`.
  - `runs` is in play order and lists only recorded runs.
  - `filter` is informational only.
- **`play-now.json`** is a request to play right away. It is either
  `{"format_version":1,"requested_at_utc":…,"playlist":"<slug>"}` or
  `{"format_version":1,"requested_at_utc":…,"runs":[{"game_start_time":…,"label":…}]}`.
  - The game **consumes** it: it reads the file, then deletes it.
  - The page treats the file still being there after about 10 seconds as
    "the game hasn't picked it up (is it at the main menu?)".
- **What the game must do:**
  - Re-check every run with `ReplayVersionGate.Check` (recording commit and
    model-id hash against the running game) and skip the ones that do not
    match. The page's "playable" mark is computed outside the game and is
    only a best guess.
  - Treat `game_start_time` as the directory name under
    `user://SpireLens/replays/`.
- **Writes are atomic.** The page writes a temp file `.<name>.<random>.tmp`,
  then renames it over the target. A reader that lists `*.json` never sees
  a partial file, so the game can ignore any name that does not end in
  `.json`.
- **Keys are snake_case**, and `format_version` is `1`. A breaking change
  would bump it.
