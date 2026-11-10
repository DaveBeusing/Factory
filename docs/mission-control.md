# Mission Control operator guide

[← Documentation](README.md) · [Getting started](getting-started.md) · [Project authorization](operator-authorization.md)

> [!NOTE]
> Layout and the sidebar version reflect the **running Factory executable**. Project-token access hides administrator-only actions; health indicators use actual observations, not simulated connectivity.

Mission Control is a browser-based operations workspace embedded in the .NET executable. Use the [Getting started](getting-started.md#start-mission-control) to start the dashboard. Node.js and npm are used by the browser test suite.

## Connection controls

The **F** brand symbol in the sidebar opens the connection-settings dialog. Its small indicator is derived from observed controller event-stream health:

| Indicator | Observed meaning |
| --- | --- |
| Green with a gentle irregular pulse | Healthy recent heartbeat |
| Amber | Stream is connecting or heartbeat is delayed |
| Red | Offline, invalid credentials or an expired heartbeat |

The authenticated event stream sends a heartbeat every three seconds. The sidebar connection text and footer report the same status. The pulse honors the operating system's reduced-motion preference. Workers appear online only after a recent worker heartbeat.

## Topbar actions

The centered **+** button beside Search opens creation shortcuts for projects, goals, jobs and workers; project-scoped credentials intentionally hide administrator creation controls. Open the command palette with **Search**, **Ctrl+K** or **Cmd+K**.

On **Overview**, **Manage queue** and **Run queue** have distinct emphasis. The primary action uses a hover/focus gradient sheen, not an artificial connection pulse. Keyboard focus remains visible, and reduced-motion settings disable animated movement. The sidebar footer displays the **current Factory assembly version**, not the .NET runtime version.

## Workspaces

| Workspace | Available operations |
| --- | --- |
| **Overview** | Health, primary queue actions, KPIs, pipeline, goals, workers, events and observed allowance |
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

## Project activity

Open **Activity** with a project token to stream recent job events for a granted project. The view shows work package titles, event types and timestamps. Internal event messages and global events remain available through administrator access.

Switching projects closes the previous stream. The controller rechecks credentials and project grants before every batch. If access changes, the stream closes and Mission Control clears the displayed events. Use **Refresh activity** to reconnect with your current grants.

## Project review

Open **Project review** with a project token and select a granted repository. Viewers can read its latest project review. Planners can run a deterministic inspection of master and use **Review & create draft goal** on a finding. The draft opens in the selected project's Goal planner. The controller rejects a replaced review or an advanced master. Project-token reviews use the static checks and have their own saved snapshots. The administrator review can also include provider advisory commentary.

## Project goal planner

Connect with a project token and open **Goal planner**. The project selector lists granted projects. A **Planner** grant in the selected project enables creating and editing draft goals, building work packages, and generating a plan with the configured provider and budget.

Save the plan to create a revision and inspect its master and open-work review before adopting it. The adoption dialog identifies the exact revision and asks for an explicit review acknowledgment and the execution mode. If another planner changes the revision, reopen **Review plan** and review the replacement before adopting.

## Project recovery

Connect through the sidebar connection control with a project token issued by `factory viewer create`. Mission Control opens **Project recovery** and lists only projects granted to that identity.

An **Operator** grant enables job actions in the selected project. Pause and stop requests for running local jobs show as pending until the runner handles them. Resuming a previously started job requires a review acknowledgment and evidence. Use **Review and requeue** to confirm an interrupted local job's checkpoint. The controller verifies the stopped runner and clean worktree before requeueing it.

A **Budget Reconciler** grant is required in the selected project to see held reservations and submit budget recovery decisions.

- **Recover executor receipt** records an independent review and evidence reason. The controller fetches the receipt from its configured executor and posts verified final usage. A missing receipt leaves the hold open so the operator can retry.
- **Settle provider reservation** records independently verified final provider totals for a held reservation. Review the observed totals, enter both verified totals and an evidence reason, and explicitly confirm any overrun. Executor-backed reservations use receipt recovery.
- **Settle goal reservation** takes verified final input and output totals and an evidence reason. Resolve any linked provider holds first. Confirm an overrun explicitly when verified usage exceeds the reservation.

The controller rechecks project access and writes the decision to the audit trail together with the ledger update. See [operator authorization](operator-authorization.md#project-goal-budget-settlement-and-executor-receipt-recovery) for grant commands and API details.

## Browser validation

GitHub Actions runs the [Chromium browser suite](../tests/mission-control/README.md) against a local Factory controller. It covers sidebar authentication and navigation, project review through plan adoption, rejection of a stale plan, account allowances in the Planner, reconciliation after a host change and remote receipt recovery with a simulated executor.

## Authentication and safety

The browser holds the controller API token in session storage for the tab. The controller authenticates administrator requests and enforces worker execution policy. Repository permissions and branch protection govern GitHub integration. Project-scoped RBAC remains separate work.
