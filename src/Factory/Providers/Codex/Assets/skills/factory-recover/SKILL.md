---
name: factory-recover
description: Diagnose and recover expired .NET Factory jobs without duplicating Git side effects.
---

# Recovery

**Preserve state and reconcile uncertain external effects before re-execution.**

## Checklist

1. Inspect `Factory status`, the recorded lease, worktree, branch, PR and checkpoint.
2. Pause admission when needed; then invoke `Factory recover` to quarantine expired leases.
3. Confirm the original runner and any external process have stopped before requeuing.
4. Reconcile Git push/PR state and provider-accounting holds independently.
5. Report the verified recovery state and unresolved risks.

> [!WARNING]
> A lost heartbeat is not proof that Git or a provider call never completed. Never blindly repeat an operation with uncertain side effects.
