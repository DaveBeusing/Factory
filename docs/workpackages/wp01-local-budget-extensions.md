# WP-01: Bounded local goal-budget extensions

- **Status:** Partial implementation, pending CI and security/concurrency review
- **Base:** `d53ca74e547b62c33d9b195ee6c9addfe0ad9e1c`
- **Risk:** High (budget authority and execution fencing)
- **Owner:** Budget and runtime
- **Target:** `master`

## Contract

A controller-local agent execution may extend its active goal reservation using the remaining unreserved goal capacity under the same transactional `StateStore.Change` write lock used for initial claims.

### In scope

- Configurable initial allocation (`GoalReservationTokens`), extension increment, low-water threshold, and per-job reservation ceiling
- Lease/generation-fenced extensions for controller-local jobs only
- Zero over-allocation across concurrent same-goal jobs
- Usage-driven extension within a turn and before a subsequent turn
- Durable `budget_extended` operational events identifying reservation, generation, grant, and new authorized total
- Fail-closed exhaustion and unchanged held-usage reconciliation behavior
- Validated settings API fields and concurrency/runtime regression tests

### Out of scope in this increment

- Remote-worker controller extension protocol, idempotency keys, delegated-grant acknowledgment and mirror synchronization
- Unbounded historical reporting beyond existing event-retention policy
- A distinct `goal_budget_extensions` relational audit table
- UI visualization of extensions and provider account allowance controls
- Independent automated security and failure-injection review

## Settings

| JSON field | Default | Semantics |
| --- | ---: | --- |
| `goalReservationTokens` | 8192 | Initial allocation per newly claimed job |
| `goalExtensionIncrementTokens` | 4096 | Maximum additional tokens per successful extension |
| `goalExtensionThresholdTokens` | 1024 | Extend when the available reservation reaches this level |
| `maxJobReservationTokens` | 65536 | Absolute sum of initial and additional grants for one attempt |

No setting is an estimate of provider account quota. Actual provider usage is separately reported and may be unavailable. Active reservations reduce the available goal capacity. Unsettled/held reservations continue to count as liabilities. Remote workers never receive these automatic extension privileges.

## Failure and recovery

The extension, reservation total and operational event are committed in one SQLite write transaction. A stale generation or expired claim cannot extend. If the provider's final usage is missing, prior reconciliation rules retain the unknown liability. Overrun observations remain recorded before cancellation; extensions do not conceal a negative balance. No database migration is introduced.

## Verification

Run the existing .NET 10 console test runner, concurrency tests, and remote-worker authorization regression tests. Additional remote authorization, retry and crash consistency work is **required before full WP-01 completion**. Do not mark this package fully integrated merely because its local slice passes tests.
