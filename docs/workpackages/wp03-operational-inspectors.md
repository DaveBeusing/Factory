# WP-03: Mission Control operational inspectors and recovery workspace

[← Work-package archive](README.md) · [Current documentation](../README.md)

> [!NOTE]
> **Historical contract (Mission Control inspectors and controls).** Preserve the original base, scope and at-the-time status below. Draft/partial labels are archival snapshots, not live PR or release status. Inspect current `master`, evidence and open work before proposing changes.

- **Status:** Partial implementation, pending browser verification
- **Base:** `d53ca74e547b62c33d9b195ee6c9addfe0ad9e1c`
- **Risk:** Medium (UI), high for exposed reconciliation flow
- **Owner:** Mission Control
- **Target:** `master`

## Contract

Improve the existing embedded .NET-hosted Mission Control without Node.js or frontend dependencies. Use authenticated controller APIs and durable state as the sole source of operational information.

## Scope delivered

- A Recovery navigation page lists blocked, failed, paused, stopped and held-budget jobs and identifies uncertain Git stages, held token liabilities and potentially live leases
- Operational Intelligence on Overview shows observed queue age, dependency bottlenecks, review/integration states and explicitly budget-blocked work
- Detailed job inspection shows owner/heartbeat, execution generation and lease status **without lease secrets**, dependencies, scopes, timestamps, checkpoint, budget liabilities and safe-next-step guidance
- Authenticated `GET /api/jobs/{id}/quality` reads only the job's own evidence directory, verifies the recorded job and commit identity, and returns check exit codes and review verdicts without raw logs; missing, stale and inconsistent evidence is not treated as a successful gate
- Goal budget inspector obtains authoritative values through `GET /api/goals/{id}/budget`, separates consumption, active/held liabilities and unreserved capacity, and shows reservation rows, per-job usage, persisted budget events, and optional durable controller grant history
- Dynamic reservation settings (initial allocation, grant increment, low-water threshold, per-job maximum) are editable only when the authenticated settings API advertises support; older controllers do not expose unsupported controls
- Explicit reviewed reconciliation invokes the existing admin-only `POST /api/goals/{id}/budget/reconcile` with double confirmation and server-side validation
- Responsive styles and error/loading states

## Authorization and operational safety

All controller mutations are authenticated and validated by the existing server. The UI does **not** implement a new RBAC system and does not assert one exists. Held usage requires independent evidence and a reason; a client-side checkbox cannot substitute for genuine review. Leases and potential Git side effects require reconciliation before retry. An evidence file path is not a quality-gate result. Displayed token utilization is goal budget liability, not provider account allowance.

## Explicitly deferred

- End-to-end remote extension history verification after WP-01 integration (the UI now renders the optional ledger response with a compatibility fallback)
- Dedicated budget-event ledger beyond the existing bounded event list
- Extended structured quality/evidence detail (safe summaries are delivered, but complete drill-down and historical check comparisons are deferred)
- Full permissions/roles, worker utilization, scenario-driven recovery actions and measured queue throughput
- Automated browser interaction and accessibility audits

## Verification

Run the existing .NET 10 build/test suite for regression coverage, including the job-evidence identity/path traversal denial test, then perform browser checks at desktop and narrow viewports: navigation, job/goal selection, budget request/loading/errors, reconciliation form confirmation, stale lease presentation, live event updates, keyboard navigation, and permission denial. **None are marked passed until executed**.

## Dependency and integration

The UI uses existing controller state, budget, reconciliation and settings endpoints. Optional extension configuration and durable history appear only when WP-01's enhanced response is available; they require integrated API verification. WP-03 can be reviewed independently of WP-01 and WP-02, but the combined workflows must be regression-tested after upstream integration. No merge or publication is authorized by this PR.
