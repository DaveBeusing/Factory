# Master-first work-package preflight

Work packages **must be based on current `master`**, never on another open PR or feature branch. Before proposing changes, Factory checks existing code, recorded jobs, supporting tests and available open GitHub PRs and issues. Partially implemented functionality should be **finished, repaired and verified** before creating an independent implementation.

## Sequence

1. Verify the project uses `master` and record the source commit.
2. Inspect source and tests already committed to `master`, plus existing Factory work packages.
3. Query the configured remote for its live `master` SHA. For GitHub origins, query that specific repository for open issues and PRs targeting `master`. Record each matching PR head SHA. Missing access is shown as unverified.
4. Determine which parts are complete, partial, defective or missing. Keyword matches alone cannot establish completeness.
5. Plan the smallest remaining work; avoid duplicate implementations of open PRs. Use separate branches/worktrees all originating from master, with explicit dependency/integration requirements.
6. Review the preflight and acknowledge existing work before adopting **every** plan, even when keyword matches are absent or the PR inventory is unavailable. If master advances, a matching PR head changes, or verified remote or inventory evidence can no longer be checked, refresh the plan.
7. Validate changes, open a PR against master, obtain required reviews and verify integration. Never rewrite existing worktrees or auto-merge.

## Runtime behavior

`WorkPackagePreflight.Inspect` uses Git commands against a resolved master SHA. It does not use feature-checkout contents to claim existing functionality. Plans retain the base commit and preflight results. Read-only planning requires a **clean master checkout**. Manual plan saves also refresh the source baseline. Git worktree creation starts from master and refuses to silently reuse a stale or mismatched worktree.

Preflight records the local commit separately from the live remote commit. It uses a read-only `git ls-remote` request and leaves local refs and the checkout alone. A confirmed mismatch or missing remote branch blocks plan generation, manual save, adoption, direct job intake and worktree preparation. A project without an origin is local-only. If the remote is unreachable, the report marks it unverified. Previously verified remote evidence must be available again before adopting that plan. The GitHub inventory uses the project’s registered origin when available, then the checkout's `origin` for older registrations without a stored origin.

The Mission Control Plan inspector displays existing source candidates, related Factory jobs and matching PRs and issues. Use **Check master & open work** to fetch the current report before writing packages. These observations are **candidates for investigation**, not a source-code audit or claim of feature completion.

Direct user-created jobs and test-audit-generated jobs must also pass this preflight. The UI asks for an existing-work review acknowledgement; the CLI requires `enqueue ... --reviewed-existing-work`. In both cases Factory records the master SHA and the preflight candidate counts in the operational event history.

A plan can be adopted using:

```powershell
Factory adopt --goal <guid> --plan-revision <revision-guid> --mode local --reviewed-existing-work
```

Only supply `--reviewed-existing-work` after inspecting the implementation and checking pending PR and issue overlap. Use the `revision` from the plan you reviewed. The API equivalent is `POST /api/plans/{goalId}/adopt` with `planRevision` and `reviewedExistingWork: true`. Read the saved plan with `GET /api/plans/{goalId}`. The planner list also supplies the current `goalRevision` for manual saves, which must include that value and the previously loaded plan `revision` when editing. `GET /api/goals/{goalId}/preflight` returns the current preflight report.

Changing the objective or ordered acceptance criteria invalidates an existing draft plan. Reopen the current goal and review and save the plan again or regenerate it. Older plan files can be opened and saved to add revisions. Once work packages exist, the objective and criteria stay bound to that work. Token budgets remain editable subject to existing liabilities.

## Limitations

GitHub PR and issue inventories are optional for local-first or non-GitHub repositories, is capped at 100 entries each, and may be unavailable. In those cases, the operator must check external work manually. The preflight cannot automatically prove that a feature is fully implemented, and it does not approve merges or deployment.

## GitHub integration policy validation

The controller checks each required context at the verified PR head. For classic branch protection and active rulesets that pin a required check to an app, a successful check run must expose the same app ID. A same-name result from another app or a commit status without verifiable app identity cannot satisfy that pin. Required workflow rules remain blocked because a check context alone cannot establish the workflow's identity.

The controller reads every page of active rules. When GitHub exposes classic required contexts but hides their source configuration, verification stops. Use a controller credential that can read the classic branch protection details for that repository.

The proposed policy for `master` requires a pull request, the `build-and-test` check from GitHub Actions, and an independent approval. Stale approvals should be dismissed on new commits. Test this policy in a disposable or explicitly authorized repository first. Verify that a missing result, an untrusted same-name result, an unapproved push and a changed PR head fail, and that the approved PR with the trusted check can merge. Record any emergency override against the exact head and the operator who authorized it. Applying repository settings requires a separate operator decision.
