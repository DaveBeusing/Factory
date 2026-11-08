---
name: factory-recover
description: Diagnose and recover expired .NET Factory jobs without duplicating Git side effects.
---
# Recovery

Read `Factory status`, inspect branch/worktree/PR and then run `Factory recover`. Expired remote jobs and interrupted pushes/PRs require a human reconciliation. Never blindly retry a job with unknown remote effects.
