---
name: dev-work
description: SpireLens development workflow for code changes, stats work, Harmony patches, PR prep, and user-invoked "dev-work" sessions. Use when modifying this repo and needing the expected git, build, deploy, live-reload, validation, commit, push, PR, and merge flow.
---

# Dev Work

## Core Rule

Treat SpireLens work as branch-based, pushed, live-validated development. Do not leave the user paying manual validation tax that the repo tooling can handle.

## Start

1. Read `AGENTS.md`, then read the task-relevant docs named there.
2. Make sure local `main` matches remote `origin/main`.
   - Update it as a ref, without checking it out: `git fetch origin main:main`.
     That fast-forwards the ref and fails loudly on divergence.
   - Never leave a worktree sitting on `main`, the primary clone included. Git
     allows one worktree per branch, so camping on `main` blocks every other
     worktree from it and breaks tooling that expects it to be free — notably
     `gh pr merge --delete-branch`, which dies with `fatal: 'main' is already
     used by worktree` after the merge has already succeeded.
   - Freeing `main` is not by itself the fix, because that same
     `--delete-branch` wants to *check `main` out*. With `main` unheld it
     succeeds and silently leaves the feature worktree camping instead of
     erroring. Not passing the flag is the fix — see **PR And Merge** step 4.
   - If a clone is found parked on `main` and is clean, `git checkout --detach`
     frees the branch without changing working-tree contents.
   - If main cannot be updated cleanly, stop and report the blocker.
3. Branch from fresh `main`, using the `codex/` prefix unless the user asks otherwise.
   - Do this when starting a new dev-work session, or after the current feature branch has been PR'd/merged and work is continuing.
   - During an ongoing user session, expect the user may provide many related fixes in sequence. Keep stacking follow-up commits on the current live feature branch unless the user explicitly asks for a separate branch.
   - Do not switch back to `main` or create sibling branches mid-session just because the user gives another issue. If the current branch feels unrelated to the new request, say so and ask before splitting.
   - Prefer a worktree for substantial work or anything likely to overlap with existing dirty state.
   - If staying in the current worktree, inspect dirty files and do not disturb unrelated user changes.

## Implement

1. Do the requested work. Read code before changing it.
2. For new attribution or Harmony hooks, read `docs/sts2-runtime-primer.md`.
3. For persisted fields, add schema fixtures and schema-loading assertions.
4. Use focused commits on the feature branch. Do not be shy about commits.
5. Push the feature branch after committing. Do not be shy about pushing feature-branch updates.

## Validate After Each Pushed Iteration

After commit and push, validate the exact pushed code path as far as local tooling allows:

1. Run the relevant unit tests.
   - Default: `dotnet test D:\repos\SpireLens\Tests\SpireLens.Core.Tests\SpireLens.Core.Tests.csproj -c Debug`
   - Add focused filters when useful, but do not replace the broad suite for risky shared changes.
2. Build/deploy the mod.
   - Core-only changes may use the core project/test build if it copies `SpireLens.Core.dll`.
   - Loader or config-page changes need the root build/deploy.
   - If deploy fails because STS2 locks `SpireLens.dll`, report that distinction; core hot-reload may still be deployable.
3. Reload SpireLens through automation, not by asking the user to press F5.
   - Prefer MCP tool `reload_spirelens_core` when available.
   - Equivalent bridge call: `POST http://localhost:15526/api/v1/singleplayer` with `{"action":"dev_reload_spirelens_core"}`.
4. Report restart requirements as evidence-based facts, never precautionary
   chores.
   - If it is certain that no restart is needed, say so.
   - If it is certain that a restart is required, say so and name the evidence.
   - Otherwise, explicitly say the restart requirement is uncertain and
     propose a concrete way to learn.
   - A newly introduced Harmony target alone does not prove that a restart is
     required. Hot reload proves installation, but existing compiled callers
     may or may not bypass the patch through inlining or devirtualization.
   - An observed post-reload behavior change proves that the relevant live path
     is already reaching the patch; do not ask for a restart in that case.
   - Under the repo's user-owned verification rule, do not restart an active
     game or assign the user a restart merely to cover this uncertainty.
5. Inspect `%APPDATA%\SlayTheSpire2\logs\godot.log`.
   - Require `Core.Initialize complete`.
   - Treat `Core.Initialize threw` as a failed validation.
   - For Harmony changes, reflection-check target method names and parameter names before relying on reload.
   - For a new Harmony target, a patched-method listing without any expected
     entry diagnostic is a restart signal before it is evidence that the hook
     target is wrong.
6. For UI/tooltip changes, use SpireLensMcp live tools/screenshots when practical.

If validation fails, fix it, commit, push, and repeat the validation loop. Ask the user for help only when automated tooling is unavailable, the game state genuinely needs human setup, or the decision is product-level.

## PR And Merge

When the user asks to PR and merge:

1. Ensure the branch is committed and pushed.
2. Create the PR.
3. Wait for the PR's CI to actually report before merging. This repo **has** a
   headless gate: `.github/workflows/dotnet-quality.yml` builds against the
   checked-in `lib/{0Harmony,GodotSharp,sts2}.dll` reference stubs and runs
   `dotnet test ... --filter "Category!=RequiresLiveGame"` on every PR and every
   push to `main`. Nothing in this repo *requires* that check to pass before
   merge, so `gh pr merge` will go straight through a gate that is still queued.
   - A checks reading of `passing: 0, failing: 0, pending: 0` taken seconds
     after `gh pr create` means GitHub has not reported yet. Treat 0/0/0 as
     unknown, never as "this repo has no CI".
   - To reproduce a gate failure locally, force the stub path the runner uses:
     `-c Release -p:SkipModsDeploy=true -p:Sts2DataDir=`. An ordinary local
     build resolves the full game install and hides headless-only failures,
     such as `GetTypes()` throwing `ReflectionTypeLoadException` against the
     stub's missing dependency closure.
4. Merge with `gh pr merge <number> --squash`. **Do not pass `--delete-branch`
   /`-d`.** Both halves of that flag are wrong here:
   - The remote half is a no-op — the repo has `delete_branch_on_merge`
     enabled, so GitHub has already deleted the branch by the time `gh` asks.
   - The local half is what causes camping. Git cannot delete the branch `HEAD`
     is on, so `gh` checks out the default branch and pulls it in order to
     delete the feature branch. That checkout is the camping, and it happens
     silently on success.
5. After merge, update local `main` as a ref: `git fetch origin main:main`. Do
   not switch a worktree to `main` to do it. Refs are shared across worktrees —
   one fetch updates `main` for the primary clone and every worktree at once, so
   there is nothing further to do "at the git root".
6. Delete the local feature branch yourself, detaching first so nothing camps:
   `git checkout --detach main && git branch -D <feature-branch>`. This also
   lands the worktree on the merged result. The remote branch needs no
   attention; it is auto-deleted on merge.
6. Rebuild and deploy Core from the merged `main`, then hot-reload. The live
   install is still running the feature-branch build, which is the merged code
   plus whatever `main` gained while the PR was open — a mixture that exists
   nowhere in the repo. Build from a worktree sitting on merged `main`
   (`git worktree add --detach <path> main`), then reload through the bridge
   and check the log, exactly as in **Validate After Each Pushed Iteration**.
   This is agent-owned work like every other deploy. Do not offer it to the
   user as an optional follow-up; `AGENTS.md` forbids handing them a rebuild or
   redeploy as a chore, and an offer is just a chore with a question mark.
7. Optional: if the primary clone's working tree is stale enough to confuse
   anyone reading it, `git -C D:\repos\spirelens checkout --detach main` moves
   its detached `HEAD` to the tip without holding the branch.

### `--delete-branch` Is Wrong On `gh pr close` Too

The camping is the flag's *local* half, which is not specific to `merge`.
Observed 2026-10-10 on throwaway PR #339: `gh pr close 339 --delete-branch`,
run from the worktree sitting on that branch, printed
`Deleted branch claude/ci-gate-probe and switched to branch main` and left the
feature worktree camping on `main` exactly as `merge -d` does.

The *remote* half differs, and that changes the cleanup. `delete_branch_on_merge`
fires only on a merge, so a closed-unmerged PR's remote branch survives — the
flag is doing real work there, not a no-op. Dropping it and stopping leaks the
remote branch. Do both sides explicitly:

```
gh pr close <number>
git checkout --detach main
git branch -D <feature-branch>
git push origin --delete <feature-branch>
```

### One PR Per Logical Change, Even From One Branch

Squash is the only merge method this repo allows, so a branch carrying several
logical commits collapses into one `main` commit. That is wrong whenever the
commits are separable work — every existing `main` commit is one focused change
with its own `(#N)`, and collapsing two of them loses that.

A session that stacks follow-up commits (the normal cadence above) therefore
often ends with more than one PR. Land them oldest-first:

1. Branch the first commit off on its own: `git branch <topic-a> <sha>`, push,
   PR, merge.
2. `git fetch origin main:main`, then rebase the working branch onto the new
   `main`. The already-merged commit drops out as an empty cherry-pick, leaving
   only the later work.
3. Confirm the remainder is what you think it is — `git diff --stat main` should
   show only the second change's files — then push with `--force-with-lease`,
   PR, merge.

Do not split a branch this way when the commits are genuinely one change (a fix
plus its test, an implementation plus its doc note). The test is whether each
commit would stand as a sensible `main` entry on its own.

### Re-read This Doc After Rebasing

`main` can gain workflow fixes mid-session. A copy of this file read at session
start goes stale the moment you rebase onto newer `main`, and the stale copy is
the one still in context. Re-read the relevant section after any rebase that
pulls in new commits, and before the PR/merge phase specifically — that is where
the guidance has changed most often.

## Communication

Be explicit about which phase you are in: branch setup, implementation, commit/push, validation, iteration, PR/merge. If a required validation step is skipped, say exactly why.
