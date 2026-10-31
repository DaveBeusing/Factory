---
name: factory-review
description: Review a Factory change and determine integration readiness.
---
# Review

Read the job's evidence file, check the named commit and base branch, and inspect the pull request. Never auto-merge. After a human merges using the repository's usual workflow, run `Factory verify <guid>` to confirm integration before dependent jobs can start.
