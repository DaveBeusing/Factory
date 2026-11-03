# WP-02: Focused SQLite state transactions

- **Status:** Partial implementation, pending CI and independent review
- **Base:** `d53ca74e547b62c33d9b195ee6c9addfe0ad9e1c`
- **Risk:** High (persistence, lease fencing)
- **Owner:** Persistence
- **Target:** `master`

## Contract

Reduce full aggregate materialization for frequent, narrowly scoped operations while preserving SQLite write serialization, foreign keys, existing schema-5 data, and execution fencing.

### In scope

- Transactional append-only operational events with existing bounded retention
- Conditional lease renewal gated by the exact job identity, execution generation, token, owner, unexpired lease, and active lifecycle state
- Idle worker heartbeat updates conditional on an enabled registration
- Targeted job reads including persisted ordered child collections
- Atomic fenced lifecycle stage updates and stage events in one transaction (without loading the aggregate)
- Targeted fenced token debits that update a job and its active goal reservation in one SQLite transaction; observed overrun is preserved
- Existing agent runtime now calls focused debits for fenced executions and records verified final usage without aggregate mutation
- Representative same-fixture console benchmark of focused versus snapshot token updates (timings are observations, not a performance guarantee)
- Rollback and stale lease denial tests for stage changes
- Local runner and idle worker protocol integration
- Concurrency and denial tests for targeted operations

### Out of scope in this increment

- Atomic SQL job claiming with goal budget reservation
- Job-specific remote protocol heartbeat accounting without aggregate reads (remote authorization and capability checks still use controller state)
- Transactional budget settlement/reconciliation rewrite
- Broader query projections and statistically robust multi-process performance evaluation
- Schema migration (none is necessary for these operations)

## Consistency and recovery

Every write uses a SQLite transaction with the existing WAL, foreign-key and timeout configuration. Failed conditions return false without affecting unrelated rows. Existing `Change` paths remain authoritative for multi-entity claims and budget operations until a separately verified atomic replacement is available. Backups remain the operator's responsibility; this change neither migrates nor deletes supported state.

## Validation

The console test suite includes targeted job retrieval, worker heartbeat authorization, generation/owner/expiration denial, atomic stage events, targeted cumulative token accounting, observed overrun, failure-injected rollback and a representative benchmark. Benchmark output records measured times on the CI runner; a single warm fixture must not be presented as a comprehensive performance study. Execute:

```powershell
dotnet restore Factory.slnx
dotnet build Factory.slnx --configuration Release --no-restore
dotnet run --project tests/Factory.Tests/Factory.Tests.csproj --configuration Release --no-restore
```

**Remaining exit gates:** Successful latest-head CI, controlled multi-process performance profiling, process-crash/failure injection, independent persistence/security review, and migration/backup regression evidence. The new token-accounting runtime path overlaps the BudgetedAgentRuntime changes in WP-01; **integrate WP-02 first, then explicitly resolve/retest WP-01** rather than combining branches without review. This increment must not be represented as full WP-02 completion.
