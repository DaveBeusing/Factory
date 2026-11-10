---
name: factory-status
description: Inspect the current .NET Factory queues, goals, workers and events.
---

# Operational status

**Report observed lifecycle states and remaining uncertainty.**

## Procedure

1. Run `Factory status` for projects, goals, jobs and workers.
2. Use `Factory analytics` for recorded activity and `Factory usage` for externally supplied usage windows when available.
3. Inspect authorized Mission Control views for queue stages, worker heartbeats and reviews.
4. Distinguish **queued**, **running**, **blocked**, **PR ready**, **merged** and **verified integrated** outcomes.

> [!NOTE]
> Unknown account allowance and absent heartbeat are not zero allowance or a confirmed stop. A completed branch is not verified integration.
