# Mission Control operator guide

Mission Control is a browser-based operations workspace embedded in the .NET executable. Use the [root README](../README.md#5-start-factory) to start the dashboard. Node.js and npm are used by the browser test suite.

## Connection controls

The **F** brand symbol in the sidebar opens the connection-settings dialog. Its small indicator is derived from observed controller event-stream health:

| Indicator | Observed meaning |
| --- | --- |
| Green with a gentle irregular pulse | Healthy recent heartbeat |
| Amber | Stream is connecting or heartbeat is delayed |
| Red | Offline, invalid credentials or an expired heartbeat |

The authenticated event stream sends a heartbeat every three seconds. The sidebar connection text and footer report the same status. The pulse honors the operating system's reduced-motion preference. Workers appear online only after a recent worker heartbeat.

## Topbar actions

An icon-only **+** button is immediately left of Search and opens creation shortcuts for projects, goals, jobs and workers. Open the command palette with Search, Ctrl+K or Cmd+K.

## Workspaces

| Workspace | Available operations |
| --- | --- |
| **Overview** | Health, operational KPIs, pipeline, goals, workers and recent events |
| **Projects** | Register, edit paths, checks and protected paths, activate, pause, disable or archive repositories |
| **Goals** | Edit objectives, acceptance criteria and token budgets. Activate, pause, stop, complete or archive goals. |
| **Goal planner** | Filter by project, check master and open work, edit named dependencies, review budget and adopt a plan |
| **Project review** | Inspect master and project health, then turn a reviewed finding into an editable draft goal |
| **Jobs** | Create/edit, prioritize, directly start eligible local jobs, inspect stages and evidence, recover remote provider receipts, request local pause/stop, acknowledge review and requeue, verify integration |
| **Queue** | Start, pause new scheduling, resume, or request cancellation of locally active jobs |
| **Workers** | Register/configure, start/stop locally managed worker processes, view real heartbeats and platform, enable, drain or disable new remote-job claims |
| **Recovery** | Review interrupted jobs, checkpoints and actions that require operator evidence |
| **Activity** | Inspect persisted lifecycle and audit events |
| **Capacity** | Inspect provider, model and role usage, local allocations, held reservations, provider-reported costs and Codex account windows |
| **Settings** | Adjust concurrency, review rounds, worker lease and other supported runtime controls |

## Planning and capacity

Start with [Project review and goal planning](project-reviews.md) for the workflow from a repository finding to an adopted plan. Review [master and open work](planning-preflight.md) before adopting packages.

Use **Projects → Inspect → Configure AI providers** to route operations to enabled profiles. **Capacity** shows attributed usage, Factory-local limits and any account windows reported by the provider. The **Goal Planner** also shows the shared controller Codex allowance next to local project capacity. See [Provider routing and budgets](providers.md) for configuration and accounting. Use the [recovery guide](recovery.md) to review interrupted jobs and settle held usage.

## Project budget recovery

Connect through the sidebar connection control with a project token issued by `factory viewer create`. Mission Control opens **Project recovery** and lists only projects granted to that identity. A Budget Reconciler grant is required in the selected project to see held reservations and submit recovery decisions.

- **Recover executor receipt** records an independent review and evidence reason. The controller fetches the receipt from its configured executor and posts verified final usage. A missing receipt leaves the hold open so the operator can retry.
- **Settle provider reservation** records independently verified final provider totals for a held reservation. Review the observed totals, enter both verified totals and an evidence reason, and explicitly confirm any overrun. Executor-backed reservations use receipt recovery.
- **Settle goal reservation** takes verified final input and output totals and an evidence reason. Resolve any linked provider holds first. Confirm an overrun explicitly when verified usage exceeds the reservation.

The controller rechecks project access and writes the decision to the audit trail together with the ledger update. See [operator authorization](operator-authorization.md#project-goal-budget-settlement-and-executor-receipt-recovery) for grant commands and API details.

## Browser validation

GitHub Actions runs the [Chromium browser suite](../tests/mission-control/README.md) against a local Factory controller. It covers sidebar authentication and navigation, project review through plan adoption, rejection of a stale plan, account allowances in the Planner, reconciliation after a host change and remote receipt recovery with a simulated executor.

## Authentication and safety

The browser holds the controller API token in session storage for the tab. The controller authenticates administrator requests and enforces worker execution policy. Repository permissions and branch protection govern GitHub integration. Project-scoped RBAC remains separate work.
