# WP-04: Master-first preflight and completion-first work packages

[← Work-package archive](README.md) · [Current documentation](../README.md)

> [!NOTE]
> **Historical contract (Master-first existing-work preflight).** Preserve the original base, scope and at-the-time status below. Draft/partial labels are archival snapshots, not live PR or release status. Inspect current `master`, evidence and open work before proposing changes.

- **Status:** Implemented in draft PR #21; integration pending
- **Target:** `master`
- **Baseline commit:** `2f8777c65cdf9eb38e5ff6c54ccfad90bf7774bd`
- **Owner:** Factory orchestration and Mission Control
- **Risk:** Medium for planning/UX; high for job admission and Git worktree creation
- **Parallel group:** Independent of provider implementation, subject to shared file integration checks

## Preflight finding

The current master planner previously captured the working checkout's HEAD and did not formally inspect already implemented source or pending open PR work. The manual plan save path also overwrote a plan's base with HEAD. Worktree preparation used a configurable base branch, with no explicit master-only rule.

Two preexisting unmerged draft PRs (#19 and #20) overlap considerably on provider routing, usage, budget, persistence and project review. Their implementation state is **not** proof of integration. This package does not reproduce or merge that code; it provides reusable controls to prevent future duplicate work.

## Outcome

All user-entered and planner-generated Factory work packages must originate from `master`; existing source, jobs and available open PRs must be inspected before new work is proposed. A partial implementation should be completed and tested, not replaced by a duplicate. Every plan adoption requires an explicit review acknowledgement, including when the heuristic finds no overlap. An operator must verify open PRs when automated inventory is unknown.

## Delivered scope

- Added master-commit-bound source/work/PR preflight with uncertainty reporting for unavailable GitHub CLI credentials.
- Plans retain a preflight snapshot and master commit, with freshness checks before adoption.
- Planner agent instructions require investigation of source, tests and pending PR work before decomposition.
- New Git worktrees start from the verified master commit and existing worktrees are not overwritten on mismatch.
- Direct CLI and Mission Control job intake require existing-work review before admission.
- Updated Mission Control plan inspector, planner skill, README and operator guide.

## Out of scope

- Automatic or unsafe PR merging, rebasing, publication or branch deletion.
- Semantic certification that an existing feature is fully implemented.
- Automatic PR inventory from non-GitHub providers.
- Changing, merging or closing PR #19/#20.
- Provider runtime/budget refactoring already under those two PRs.

## Acceptance

- [x] New plan and direct-job paths require master.
- [x] Master source inspection is independent of a checked-out feature branch.
- [x] Existing-code candidates are visible and every plan adoption requires explicit review.
- [x] Advance of master invalidates a saved plan.
- [x] Existing worktrees are preserved on ancestry/branch mismatch.
- [x] Existing independent tests cover master/feature checkout distinction and direct intake.
- [ ] Verify latest current-head GitHub Actions build/test/publish.
- [ ] Complete independent security/integration review and merge under repository policy.

## Recovery and handover

Back up Factory state and preserve existing Git worktrees. If the preflight blocks a job, inspect master, reconcile overlapping open PRs and explicitly update/approve the plan rather than resetting the workspace. No release or merge is authorized by the draft PR.
