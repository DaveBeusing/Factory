# Master-first work-package preflight

Work packages **must be based on current `master`**, never on another open PR or feature branch. Before proposing changes, Factory checks existing code, recorded jobs, supporting tests and available open GitHub PRs. Partially implemented functionality should be **finished, repaired and verified** before creating an independent implementation.

## Sequence

1. Verify the project uses `master` and record the source commit.
2. Inspect source and tests already committed to `master`, plus existing Factory work packages.
3. When a GitHub origin and authenticated `gh` are available, inspect open PR titles targeting `master`. Missing access remains **unverified**, not “zero PRs”.
4. Determine which parts are complete, partial, defective or missing. Keyword matches alone cannot establish completeness.
5. Plan the smallest remaining work; avoid duplicate implementations of open PRs. Use separate branches/worktrees all originating from master, with explicit dependency/integration requirements.
6. Review the preflight and acknowledge existing work before adopting the plan. If master advances or a previously verified matching PR inventory changes, refresh the plan.
7. Validate changes, open a PR against master, obtain required reviews and verify integration. Never rewrite existing worktrees or auto-merge.

## Runtime behavior

`WorkPackagePreflight.Inspect` uses Git commands against a resolved master SHA. It does not use feature-checkout contents to claim existing functionality. Plans retain the base commit and preflight results. Read-only planning requires a **clean master checkout**. Manual plan saves also refresh the source baseline. Git worktree creation starts from master and refuses to silently reuse a stale or mismatched worktree.

The Mission Control Plan inspector displays existing source candidates, related Factory jobs and matching PRs. These observations are **candidates for investigation**, not a source-code audit or claim of feature completion.

A plan can be adopted using:

```powershell
Factory adopt --goal <guid> --mode local --reviewed-existing-work
```

Only supply `--reviewed-existing-work` after inspecting the implementation and checking pending PR overlap. The API equivalent is `POST /api/plans/{goalId}/adopt` with `reviewedExistingWork: true`. `GET /api/goals/{goalId}/preflight` returns the current report.

## Limitations

GitHub PR inventory is optional for local-first or non-GitHub repositories, is capped at 100 open PRs, and may be unavailable. In those cases, the operator must check external work manually. The preflight cannot automatically prove that a feature is fully implemented, and it does not approve merges or deployment.
