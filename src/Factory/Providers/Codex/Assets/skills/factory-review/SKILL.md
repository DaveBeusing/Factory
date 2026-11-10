---
name: factory-review
description: Review a Factory change and determine integration readiness.
---

# Integration review

**Evaluate the exact source revision, not an older passing result.**

## Procedure

1. Read the job's evidence file and identify evaluated commit SHA, expected base, branch and scope.
2. Inspect the real diff and the pull request. Confirm mandatory checks and review results are current for that commit.
3. Report findings and distinguish **PR ready** from **merged**.
4. After a separate, authorized human merge, invoke `Factory verify <job-guid>` to confirm integration before dependent jobs start.

> [!CAUTION]
> Never self-approve or auto-merge based only on a generated review verdict. Missing, outdated or contradictory evidence blocks readiness.
