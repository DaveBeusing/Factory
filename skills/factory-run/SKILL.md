---
name: factory-run
description: Run the local .NET Factory job queue with deterministic gates and strict master-based work-package admission.
---
# Factory run

Use the compiled `Factory` .NET 10 executable, not Python. Locate the installed distribution or ask for its path.

1. Read `Factory doctor` and `Factory status` first.
2. Verify each work package was planned against the current **master** commit, with existing code/tests and pending PRs inspected. Do not recreate a feature already complete or present in another open PR.
3. For direct jobs, run the existing-work preflight before `Factory enqueue`; only use `--reviewed-existing-work` after reviewing overlap candidates. For plan-generated jobs, obtain explicit preflight review before adoption.
4. For new jobs with no explicit execution mode, ask local or remote. Never assume remote publication.
5. Invoke `Factory run` only when the user requested execution.
6. Factory owns worktrees, commits, checks, reviews, retries and statuses. Preserve unfinished code and do not bypass its gates or silently rebase/reset existing branches.
7. Summarize actual states and links; never claim merge, integration or deployment without independent verification.
