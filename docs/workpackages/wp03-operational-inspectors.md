# WP-03: Mission Control operational inspectors and recovery workspace

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
- Goal budget inspector obtains authoritative values through `GET /api/goals/{id}/budget`, separates consumption, active/held liabilities and unreserved capacity, and shows reservation rows and persisted budget events
- Explicit reviewed reconciliation invokes the existing admin-only `POST /api/goals/{id}/budget/reconcile` with double confirmation and server-side validation
- Responsive styles and error/loading states

## Authorization and operational safety

All controller mutations are authenticated and validated by the existing server. The UI does **not** implement a new RBAC system and does not assert one exists. Held usage requires independent evidence and a reason; a client-side checkbox cannot substitute for genuine review. Leases and potential Git side effects require reconciliation before retry. An evidence file path is not a quality-gate result. Displayed token utilization is goal budget liability, not provider account allowance.

## Explicitly deferred

- End-to-end remote extension history dependent on WP-01
- Dedicated budget-event ledger beyond the existing bounded event list
- Typed server-side detailed quality/evidence query endpoints
- Full permissions/roles, worker utilization, scenario-driven recovery actions and measured queue throughput
- Automated browser interaction and accessibility audits

## Verification

Run the existing .NET 10 build/test suite for regression coverage, then perform browser checks at desktop and narrow viewports: navigation, job/goal selection, budget request/loading/errors, reconciliation form confirmation, stale lease presentation, live event updates, keyboard navigation, and permission denial. **None are marked passed until executed**.

## Dependency and integration

The UI uses only APIs already present in the inspected `master` baseline. It can be reviewed independently of WP-01 and WP-02. Additional extension visualization becomes available automatically when the eventual backend emits `budget_extended` events. No merge or publication is authorized by this PR.
