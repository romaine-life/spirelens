# Steam Workshop publishing

SpireLens is published on the Steam Workshop as item
[3774710835](https://steamcommunity.com/sharedfiles/filedetails/?id=3774710835)
(first upload 2026-07-30, visibility private until flipped public on the item page).

## Workspace

Publishing happens from a standalone workspace **outside this repo** (binaries, not committed):

```
D:\repos\spirelens-workshop\
├── ModUploader.exe      Megacrit's official uploader (v0.2.0)
├── steam_api64.dll      ships with the uploader
├── workshop.json        Workshop metadata (changeNote, deps; title/description/visibility null — see below)
├── image.png            thumbnail, PNG, must be < 1MB (Steam backend limit)
├── mod_id.txt           3774710835 — written by the first upload; makes later runs update instead of create
└── content\
    └── SpireLens\       exact copy of the deployed mod folder
        ├── SpireLens.json
        ├── SpireLens.dll
        ├── SpireLens.Core.dll
        └── Mono.Cecil*.dll (4 files)
```

- Uploader source/releases: <https://github.com/megacrit/sts2-mod-uploader>
- `content\` nests the `SpireLens\` mod folder (matches how BaseLib's workshop item is laid out;
  subscribed items land at `steamapps\workshop\content\2868840\<item-id>\<ModName>\`).
- `workshop.json` `dependencies` is a list of **numeric workshop item IDs**. BaseLib = `3737335127`.
  The in-game dependency (`SpireLens.json` → `"id": "BaseLib"`) is separate and also required.

## Normal release flow

The normal release trigger is a merged version bump, not a manually pushed tag:

1. Put the player-facing bullets in each feature pull request's `## Workshop notes` section.
2. Open and merge a release pull request that bumps `SpireLens.json` to the next version.
3. After the merged `main` checks succeed on Forgejo, the release job creates the matching tag.
4. That job uploads the same package and changelog to Steam, then publishes the Forgejo release.

To retry a failed publication, dispatch `.forgejo/workflows/ci.yml` on the same
`main` commit. Completed versions are left intact.

## Manual workspace publishing

1. Bump `"version"` in [SpireLens.json](../SpireLens.json) (repo root, source of truth). SemVer-ish:
   minor for features, patch for fixes. The string is shown to subscribers in-game.
2. Download the released Forgejo package into the workspace:

   ```powershell
   $release = Invoke-RestMethod 'https://forgejo.romaine.life/api/v1/repos/romaine-life/spirelens/releases/latest'
   $archive = Join-Path $env:TEMP 'SpireLens.zip'
   Invoke-WebRequest -Uri ($release.assets | Where-Object name -eq 'SpireLens.zip').browser_download_url -OutFile $archive
   Expand-Archive -LiteralPath $archive -DestinationPath 'D:\repos\spirelens-workshop\content' -Force
   Remove-Item -LiteralPath $archive
   ```

   (The signed package built by
   [.forgejo/workflows/ci.yml](../.forgejo/workflows/ci.yml).)
3. Set `changeNote` in `workshop.json`.
4. Steam client running and logged in, then:

   ```powershell
   Set-Location "D:\repos\spirelens-workshop"; .\ModUploader.exe upload -w "D:\repos\spirelens-workshop"
   ```

Success looks like `Status: k_EItemUpdateStatusCommittingChanges` →
`Successfully uploaded 'SpireLens' ...`. The trailing `k_EItemUpdateStatusInvalid`
line is the SDK's idle state, not an error.

## Automated releases

[The Forgejo workflow](../.forgejo/workflows/ci.yml) publishes the exact
packaged mod to Workshop item `3774710835`. A successful merged `main` build
publishes the manifest's version when it has not been released yet. The publication job:

1. requires the tag to exactly match `SpireLens.json`'s version;
2. validates the package allowlist;
3. restores an isolated SteamCMD login from the Forgejo repository's Actions secret;
4. builds one set of player-facing notes and uses it for both the Forgejo
   release and Workshop change note;
5. uploads only content and a change note, leaving page-managed metadata alone;
6. serializes uploads so two tags cannot update the item concurrently.

Release notes come from every merged pull request between the previous tag and
the new tag. Put player-facing bullets in the pull request's
`## Workshop notes` section. If the section is absent or empty, the pull request
title is used as a fallback. Maintenance-only pull requests can opt out by
putting exactly `No player-facing changes.` in that section. Direct commits that
were not associated with a pull request fall back to their commit subject.

The Steam VDF keeps the notes' real line-feed characters inside the quoted
`changenote` value. Do not replace them with a literal `\n`: Steam displays
those two characters verbatim instead of creating a line break.

The publication job runs only on `main` after both the Go and mod checks pass.

Bootstrap or refresh that Forgejo credential from a trusted Windows machine:

```powershell
.\scripts\bootstrap-steam-workshop-secret.ps1 -SteamUsername <steam-login-name>
```

The script opens SteamCMD for one interactive password/Steam Guard login. It
stores the resulting isolated `config.vdf` as the Forgejo Actions secret
`STEAM_CONFIG_VDF_B64` and the non-secret login name as `STEAM_USERNAME`.
It does **not** upload the Steam password or Steam Guard code. Do not substitute
the desktop Steam client's broad `config.vdf`; use the isolated credential made
by this script.

The Workshop upload itself runs only in the main-branch publication job. Pull
request jobs never restore the publishing credential. The isolated credential is
removed from the runner after publication, including failed attempts.

## workshop.json clobbers page edits

Every upload pushes every **non-null** `workshop.json` field to Steam — including title,
description, and visibility. An upload with those fields set will silently overwrite anything
edited on the item's web page (this ate a page-written description once). The workspace file
therefore keeps `title`, `description`, and `visibility` at `null` — those are managed on the
item page — and only `changeNote`, `tags`, `dependencies`, and `contentDescriptors` live in
the file. Null/omitted fields are left untouched by the uploader.

## Gotchas observed on first publish

- **"There was a problem accessing the item"** on the item page right after upload — combination of
  propagation lag and Steam's automated content scan. New commits show *"awaiting analysis by our
  automated content check system"* and stay hidden until the scan passes (seconds-to-hours; no SLA;
  every commit re-triggers it). Not an upload failure — the uploader reading the item back
  ("Querying existing workshop item details") proves it exists.
- Anonymous probes are useless while the item is private: the page shows the same generic error,
  and `ISteamRemoteStorage/GetPublishedFileDetails` returns `result: 9` regardless of scan state.
- The `SpireLens.json` version string exists in three places once deployed: repo root, the deployed
  copy under the game's `mods\`, and the workspace `content\` copy. Repo is the source of truth;
  the other two are overwritten by deploy/copy.
