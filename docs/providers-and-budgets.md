# AI provider routing and budgets

Factory keeps orchestration and project contracts provider-independent. Provider routing is project-scoped and role-scoped. The default provider is **Codex**; supported assignments can be changed from Mission Control's **Projects → Providers** dialog.

## Supported adapter capabilities

| Adapter | Planning | Project analysis | Implementation | Reviews | Remote execution | Usage | Account allowance |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Codex CLI | Read-only | Read-only | Workspace-write | Read-only | Existing Codex worker path | When the runtime reports it | Weekly/short windows only when authentically reported |
| Claude Code CLI | Read-only Plan mode | Read-only Plan mode | Not supported | Read-only Plan mode | Not supported | When structured CLI JSON reports it | Not available |

Claude Code is invoked in non-interactive print mode with `--permission-mode plan` and a restrictive tool list (`Read,Glob,Grep`). This is **not** an operating-system sandbox. Factory fails closed for write-enabled Claude jobs or unverified remote provider capabilities. Authentication stays with the respective native CLI; no provider credentials are stored in Factory project routing.

Install and authenticate the external CLI separately. For Windows CLI installations that are not discoverable on `PATH`, set `FACTORY_CLAUDE_EXECUTABLE` to a trusted executable or command wrapper. The existing `FACTORY_AGENT_EXECUTABLE` continues to apply only to Codex. No Node.js/npm dependency is added to Factory's build.

### Roles and fallback

Routes can be assigned for `planning`, `analysis`, `implementation`, `review`, and `review:code`, `review:architecture`, `review:security`, `review:ux`. Specialized reviews inherit the `review` route, and absent assignments default to Codex. For Codex routes with an empty model override, legacy risk-based model selection applies; empty model selections do **not** prove which model the external CLI chose.

Examples:

| Role | Provider | Model |
| --- | --- | --- |
| Planning | Claude Code | sonnet |
| Analysis | Claude Code | sonnet |
| Implementation | Codex | provider default |
| General review | Claude Code | opus |
| Security review | Codex | provider default |

Provider routing is a project policy choice. Failed executions do not silently switch vendors, identities or permission modes.

## Provider-level token budgets

The existing goal-budget ledger remains authoritative for total goal allocations. The new provider ledger independently handles **project × provider** allocations across all models and roles.

- Set an optional nonnegative token limit for each provider in **Capacity → Edit budget**; zero means no *Factory-side* cap.
- Before every metered job turn or read-only project-level planning/analysis call, Factory reserves provider capacity in a SQLite transaction. Concurrent reservations cannot promise the same remaining tokens.
- Provider-reported input and output deltas debit the reservation. Cache-read and cache-write input tokens are included when Claude reports them.
- A confirmed final usage report settles a successful turn and releases unused reserved capacity. Interrupted, unverified or unfinished turns remain **held**, including after a process failure.
- Held provider turns can be reconciled explicitly from **Capacity → Reconcile held usage**, but only after the operator independently verifies final input/output counts and records a justification. Missing evidence must not trigger automatic release.
- A provider limit blocks admission when remaining capacity is below the required minimum and cancels execution when the active reservation is exhausted. Goal and provider usage debits during fenced local execution share an SQLite transaction.
- The **Capacity** view reports consumed tokens, outstanding liabilities, configured limit and locally available allocation. It also groups usage by model and agent role.
- Provider-reported USD cost is shown only when available from the provider (currently Claude JSON). Missing pricing/cost and unknown model IDs remain unknown; Factory does not synthesize prices.

**Do not confuse local budget availability with external quotas.** Codex weekly/short account allowance appears only when the original usage monitor returns real data; cached readings are marked accordingly. No provider API for Claude Code remaining account allowance is implemented. A locally unbounded budget never implies infinite vendor capacity.

### API

Authenticated controller endpoints:

- `GET /api/providers`: supported adapter capabilities
- `GET /api/projects/{id}/providers`: project role assignments
- `PUT /api/projects/{id}/providers`: replace assignments with a `{ "routes": [...] }` body
- `GET /api/projects/{id}/provider-capacity`: actual recorded usage and local balances
- `PUT /api/projects/{id}/provider-budgets/{providerId}`: set `{ "tokenLimit": 100000 }`

All mutations require the existing controller administration token. Project-scoped user RBAC is **not** implemented by these endpoints.

### State migration and operational safety

Factory upgrades existing SQLite schema 6 to 7 by adding project role assignments, provider limits and provider-turn history. It retains previously recorded goal budget rows and job histories. Back up the `.factory` state directory before upgrading any valued installation; do not copy an active WAL database without a consistent SQLite backup. The schema upgrade is not a reversible downgrade path.

Historical job usage cannot be reliably attributed to a provider and is **not** backfilled with fabricated identities. Existing unconfirmed goal reservations stay subject to controller reconciliation. Other CLI actions not routed through the metered planning/review/job path may still lack provider-level accounting and must not be represented as fully captured.

See [Project reviews](project-reviews.md) for the analysis-to-goal workflow and [README](../README.md) for setup.
