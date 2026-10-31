---
name: factory-run
description: Run the local .NET Factory job queue with deterministic gates and explicit Git integration controls.
---
# Factory run

Use the compiled `Factory` .NET 10 executable, not Python. Locate the installed distribution or ask for its path.

1. Read `Factory doctor` and `Factory status` first.
2. For new jobs with no explicit branch mode, ask local or remote. Never assume remote publication.
3. Invoke `Factory run` only when the user requested execution.
4. The Factory owns worktrees, commits, reviews, retries and status. Do not bypass its gates.
5. Summarize factual statuses and links; never claim a merge or deployment without verification.
