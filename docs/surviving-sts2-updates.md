# Surviving Slay the Spire 2 updates

Slay the Spire 2 ships an update roughly **every two weeks**. Each update can:

- rename or remove the game C# symbols the **bridge** (`spirelens-mcp`) and the
  **core mod** (`spirelens`) compile and reflect against, and
- desync the third-party **BaseLib** mod, whose Harmony patches hard-reference
  game symbols that the update may have moved.

This runbook captures the recurring breakages, how to tell them apart, the fix
patterns, and a post-update checklist. The concrete examples are from the
**2026-05** cycle (STS2 `0.1.0+d3584805…`, banner `[v0.106.1]`), but the shapes
recur every cycle.

## TL;DR triage

| What you see | Likely cause | Jump to |
|---|---|---|
| `build.ps1` fails: *"X does not contain a definition for Y"* | game API drift | [Failure mode 1](#failure-mode-1-bridge--core-build-drift) |
| Run passes, model JSON is correct, but **screenshots show a frozen/placeholder HUD** (e.g. `88/88` HP, `0/4` energy) | BaseLib too old for the new game build | [Failure mode 2](#failure-mode-2-baselib-version-desync-frozen-hud) |
| HUD stale only on **debug-loaded** combat saves, BaseLib is current | `CombatStateChanged` not raised on debug load | [Failure mode 3](#failure-mode-3-hud-desync-on-debug-loaded-saves) |
| Build is clean but a stat stops recording; `godot.log` shows `TargetMethod() returned an unexpected result: null` | a runtime-bound patch's target was renamed | [Failure mode 4](#failure-mode-4-runtime-bound-patch-drift) |
| "Error loading mod" badge in the corner | old-form manifest `dependencies`, not a crash | [Failure mode 4](#failure-mode-4-runtime-bound-patch-drift) |

**Golden rule:** after a game update, **look at the screenshots, not just the
model JSON.** Modes 2 and 3 leave `get_game_state` / the model perfectly correct
while the rendered HUD lies. Pixels are the only signal that catches them.

## Two repos — don't cross them up

| Concern | Repo | Key paths |
|---|---|---|
| Bridge mod build + deploy | `romaine-life/spirelens-mcp` | `build.ps1`, `McpMod*.cs`, `mod_manifest.json` |
| Core mod | `romaine-life/spirelens` | `Core/`, `Loader/`, `SpireLens.csproj` |
| Third-party utility lib | `Alchyr/BaseLib-StS2` (vendored on host) | host `mods\BaseLib\` only |

## Failure mode 1: bridge / core build drift

**Symptom.** `build.ps1` fails compiling against `sts2.dll`, e.g.:

```
error CS1061: 'MerchantRoom' does not contain a definition for 'Inventory'
```

**Cause.** The game refactored a public member between versions. Our code
references the old shape.

**Diagnose.** Decompile the *current* `sts2.dll` and find the replacement:

- Game dir with the dll: `D:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\sts2.dll`
  (note: the other Steam library `D:\Programs\SteamLibrary\…` does **not** contain
  `sts2.dll` — `Sts2HostPaths.ps1` skips it).
- Tool on the host: `ilspycmd` (`C:\Users\nelsonlaptopuser\.dotnet\tools\ilspycmd.exe`).
  Decompile the type and read the new members.

**Fix.** Update the references, preferring **null-graceful** equivalents over
index access so a future shape change degrades instead of throwing.

> **2026-05 example (spirelens-mcp#10).** `MerchantRoom.Inventory` was replaced
> by `MerchantRoom.Inventories` (a `List`) plus `GetLocalInventory()`. Fixed in
> `McpMod.Actions.cs` and `McpMod.StateBuilder.cs` by reading
> `Inventories.FirstOrDefault()` — preserves the old graceful-null behavior
> rather than `Inventories[0]` (which would throw on an empty list).

Rebuild and redeploy (see [Deploy mechanics](#deploy-mechanics-gotchas) — the
game **must be closed** to replace the DLL).

## Failure mode 2: BaseLib version desync (frozen HUD)

**Symptom.** The pipeline passes. `get_game_state` and the model are correct
(e.g. player `76/80`, enemy `56/56`, energy `4/4`). But the **screenshot** shows
the combat HUD frozen at placeholder values — HP bars stuck at e.g. `88/88`,
energy `0/4`. The game log / bridge surfaces:

```
MissingMethodException: Creature.get_ShowsInfiniteHp()
```

**Cause.** BaseLib patches the combat HP display and **hard-references a game
symbol the update renamed**. Its patch then throws on every HP-bar refresh,
pinning the bars at their placeholder. **This is third-party code**
(`Alchyr/BaseLib-StS2`), not ours — but it breaks our screenshot evidence.

The same failure shape can block input rather than HUD rendering. In STS2
v0.110.1, `MegaInput.releaseCard` was removed. BaseLib v3.3.7 referenced that
field from its `NPlayerHand.StartCardPlay` safety patch, so selecting any card
threw `MissingFieldException` before play could start. BaseLib v3.4.0's
beta-compat update changed the fallback to the current input field.

> **2026-05 example.** STS2 renamed `Creature.ShowsInfiniteHp` →
> `Creature.HpDisplay`. The deployed **BaseLib v3.0.9** referenced the old
> getter and threw on every bar refresh. **BaseLib v3.1.8** ("main branch
> compatibility") added a graceful `HpDisplay` reflection fallback and fixed it.
> v3.1.8 *alone* resolved the freeze — the game's own post-load refresh stopped
> throwing — so the bars render correctly even before any manual repaint.

**Diagnose.**

- Confirm the dead symbol lives **only** in `BaseLib.dll`, not our mods
  (decompile / string-search the loaded mod DLLs). If only BaseLib references
  it, it's this mode.
- Check the deployed version: `mods\BaseLib\BaseLib.json` → `.version`.

**Fix.**

1. Download the BaseLib release that adds compatibility for the new game build
   (from `Alchyr/BaseLib-StS2` releases).
2. Deploy `BaseLib.dll` / `BaseLib.json` / `BaseLib.pck` into host
   `mods\BaseLib\` (game **closed**), back up the old ones.
3. Confirm `BaseLib.json` reports the new version.
## Failure mode 3: HUD desync on debug-loaded saves

**Symptom.** The HUD renders stale placeholders right after a scenario load,
*even with a current BaseLib*.

**Cause.** Debug-loading a mid-combat save does **not** raise the game's
`CombatStateChanged` signal, so combat-HUD widgets can render the placeholder
they were constructed with instead of the loaded state.

**Mitigation.** The bridge exposes a dev action `dev_refresh_combat_view`
(`spirelens-mcp#10`) that re-fires the HUD refresh on each creature display
(HP bars), the energy counter, and the visible hand via reflection, and reports
per-creature `display_bound_matches_entity` so the caller can confirm the
widgets are bound to the live entities.

With BaseLib at/above the floor this is **belt-and-suspenders** — the game's own
post-load refresh succeeds — but keep it as an explicit repaint for robustness
and as a diagnostic (its per-creature report is a quick way to see whether the
displays are bound to the right entities).

## Failure mode 4: runtime-bound patch drift

**Symptom.** The core mod builds cleanly, yet a relic or power stat stops
recording. `godot.log` has `Patch install failed for <class>` with a
`HarmonyException`: `TargetMethod() returned an unexpected result: null`, and
`Patch install: N classes patched` comes up short.

**Cause.** `[HarmonyPatch(typeof(X), nameof(X.Y))]` patches are compile-checked
against `sts2.dll`, but most relic and power patches under `Core/Patches/` find
their target at load time through `AccessTools.TypeByName` +
`AccessTools.Method` in `TargetMethod()`, so a renamed hook only fails in the
running game. `CoreMain` installs patch classes one at a time, so one dead
target costs that patch, not the mod.

**Diagnose.** List every runtime-bound patch
(`git grep -nE 'TypeByName|AccessTools\.Method|TargetMethod' Core/`) and check
each target still exists: `ilspycmd "<Sts2DataDir>\sts2.dll" -t MegaCrit.Sts2.Core.Models.Relics.<Relic>`.

> **2026-05 example (v0.106.1).** The relic end-of-turn hook `BeforeTurnEnd`
> became `BeforeSideTurnEnd` (alongside `BeforeSideTurnEndVeryEarly`); per-side
> turn hooks have been gaining `Side`.

**The "Error loading mod" badge is not this.** The game raises it
(`MOD_ERROR.MIGRATION_REQUIRED`, in `ModManifest`) when a manifest's
`dependencies` is the old bare string array (`["BaseLib"]`); the current form is
objects (`[{ "id": "BaseLib", "min_version": "3.4.0" }]`), as in `SpireLens.json`,
which also declares `min_game_version`. BaseLib's own `[BaseLib] HarmonyException`
lines are caught by its `TryPatchAll` and don't raise the badge.

## Deploy mechanics gotchas

- **Close the game before replacing any mod DLL.** STS2 holds an exclusive lock
  on loaded mod DLLs; copying over one fails with a file-in-use error. Stop it
  first: `Stop-Process -Name SlayTheSpire2 -Force` (then a short sleep).
- **Bridge build:** `build.ps1 -GameDir "D:\SteamLibrary\steamapps\common\Slay the Spire 2"`
  → `out\SpireLensMcpBridge\SpireLensMcpBridge.dll`. The `.csproj` references the
  game dll at `$(STS2GameDir)/data_sts2_windows_x86_64/sts2.dll`.
- **Bridge deploy:** copy the built DLL + `mod_manifest.json` →
  `mods\SpireLensMcpBridge.json` into the game `mods\` dir. (`prepare-host.ps1
  -InstallMcp` does this per run in the live path.)
- **Pushing files to the host over SSH:** base64 in ~6000-char chunks via
  `Add-Content`, reassemble with `[Convert]::FromBase64String`; pull back with
  `[Convert]::ToBase64String([IO.File]::ReadAllBytes(...))`. Single-line
  `pwsh -NoProfile -Command "...; ..."` works; multi-line/heredoc bodies fail
  silently over this transport.
- **`mods\BaseLib\` and `mods\SpireLens\` are host-local persistent state** — not
  reinstalled per run, and the per-run checkout sync never `git clean`s the game
  `mods/` dir. A correct deploy is durable; so is a stale one. Only
  `SpireLensMcp` is deployed per run.

## After every game update — checklist

1. **Rebuild the bridge** against the new `sts2.dll`; fix any build drift
   ([mode 1](#failure-mode-1-bridge--core-build-drift)). Same for the core mod if
   it fails to build.
2. **Check every runtime-bound patch target** in the new `sts2.dll`
   ([mode 4](#failure-mode-4-runtime-bound-patch-drift)); a clean build doesn't
   cover them.
3. **Run the verify leg and inspect the before/after screenshots**, not just the
   model JSON — modes 2 and 3 only show in pixels.
4. **If the HUD is frozen**, update BaseLib to the current compat release
   ([mode 2](#failure-mode-2-baselib-version-desync-frozen-hud)).
5. **Update any pinned version references** (e.g. an expected `sts2.dll` product
   version, if pinned anywhere) so they don't false-alarm.
