# WP-01: Dynamic goal budget allocations

- **Status:** Implemented in a draft PR; review and end-to-end acceptance pending
- **Base:** `d53ca74e547b62c33d9b195ee6c9addfe0ad9e1c`
- **Risk:** High (token accounting, worker authority, database migrations)
- **Owner:** Budget, controller, and runtime
- **Target:** `master`
- **Dependency:** No upstream merge is needed for this PR. Mission Control's enhanced presentation in WP-03 consumes these API fields when WP-01 is integrated.

## Contract and delivered scope

A job receives an initial, goal-backed token reservation when claimed. Additional capacity may be granted only from the **remaining unreserved goal budget** and only while the exact execution lease and generation remain valid. The configured goal limit is authoritative; neither the agent runtime nor a remote worker may create new goal capacity.

- `goalReservationTokens` (default 8,192): initial tokens per job claim
- `goalExtensionIncrementTokens` (default 4,096): maximum tokens granted by one extension request
- `goalExtensionThresholdTokens` (default 1,024): low-water mark checked ahead of the next agent turn
- `maxJobReservationTokens` (default 65,536): absolute per-job reservation ceiling for one execution

The local runner can extend its fenced reservation transactionally before or during an agent turn when provider usage notifications are reported. The extension and its durable history are written in the same SQLite transaction.

A remote worker may call **POST `/api/worker/budget/extend`** with its worker-scoped credential, matching worker identity header, job ID, execution generation, lease token, stable request ID and requested amount. The controller verifies the active lease, worker identity, project authorization and capability attestations. It computes and atomically persists any grant, including a zero-token denial-of-capacity result. A repeated request ID returns the prior grant without reserving more tokens. An expired, superseded or unauthorized lease is rejected.

The remote runner requests a controller grant before the next agent turn when its remaining local delegated capacity is low. Only a verified controller response may increase the worker-local mirrored reservation and synthetic delegated goal budget. On transport uncertainty, the request ID is retained for safe replay; an unavailable or denied authorization fails closed.

## Persistence and API

- Schema version **6** adds relational `goal_budget_extensions` history, linked by foreign keys to the goal, job and reservation, with grant amount, authorized total, worker, execution generation and timestamp.
- The existing schema-4/5 upgrade paths preserve reservations and upgrade transactionally. Schema-5-to-6 does not rewrite existing usage data. Earlier extensions written only to bounded events cannot be reconstructed into the new history.
- `GoalBudgetLedger.Summary` now reports total budget, consumed tokens, active and held outstanding liabilities, available capacity, reconciliation presence, per-job observed usage and durable extension records.
- Existing budget reconciliation remains explicit and independently reviewed. Final provider usage that is missing or uncertain remains a held liability; overrun observations are not silently erased.
- Provider-reported usage is **not** the same as reserved goal capacity, and neither is an estimate of external weekly/account allowances. Such allowances are displayed only when the provider actually reports them.

## Concurrency and failure behavior

All claim, extension and reservation mutations use the existing SQLite transactional serialization. The grant is bound to the execution's generation and reservation ID. Concurrent workers cannot allocate the same remaining capacity. Retry of a given remote request ID is idempotent only while that execution remains valid; an expired lease is **not** revived to replay a grant.

The remote runner currently checks for more capacity **between agent turns**, not continuously during a single remote provider turn. A long-running turn may still exhaust its granted limit and stop. This is conservative, not a silent permission to exceed the cap. A richer mid-turn remote grant/streaming authorization contract would require separate design and verification.

A crash between a committed controller grant and the worker's local mirror update can be reconciled by retrying the same request ID after restoring the still-valid execution lease; an uncertain or expired execution remains blocked for operator review.

## Verification and risk gates

The console regression suite includes concurrent same-goal allocations for three independent jobs, local runtime extension behavior, durable extension history, schema-5-to-6 preservation, and controller-authorized remote requests with idempotent replay, stale generation, invalid worker and expired lease denial.

Run:
```powershell
dotnet restore Factory.slnx
dotnet build Factory.slnx --configuration Release --no-restore
dotnet run --project tests/Factory.Tests/Factory.Tests.csproj --configuration Release --no-restore
```

GitHub Actions on the PR supplies executable build/test evidence where runs complete successfully. **Still required before marking this work package fully accepted:** independent persistence/security review, controlled remote-worker HTTP end-to-end exercise, failure injection around lost responses and process crashes, and verification of the latest PR commit.

## Backup, recovery and integration

Before upgrading an existing installation, create a consistent backup of `.factory/factory.sqlite3` and its WAL state using SQLite's backup mechanism or by stopping Factory. A schema-6 database should not be opened with older application binaries; restore a matching backup for rollback. No destructive migration, automatic merge, deployment, or release is part of this PR.

This branch is a **draft implementation**, not an approved, merged, or verified-integrated release.
