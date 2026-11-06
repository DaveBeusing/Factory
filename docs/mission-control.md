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
| **Jobs** | Create/edit, prioritize, directly start eligible local jobs, inspect stages and evidence, request local pause/stop, acknowledge review and requeue, verify integration |
| **Queue** | Start, pause new scheduling, resume, or request cancellation of locally active jobs |
| **Workers** | Register/configure, start/stop locally managed worker processes, view real heartbeats and platform, enable, drain or disable new remote-job claims |
| **Recovery** | Review interrupted jobs, checkpoints and actions that require operator evidence |
| **Activity** | Inspect persisted lifecycle and audit events |
| **Capacity** | Inspect provider, model and role usage, local allocations, held reservations, provider-reported costs and Codex account windows |
| **Settings** | Adjust concurrency, review rounds, worker lease and other supported runtime controls |

## Planning and capacity

Start with [Project review and goal planning](project-reviews.md) for the workflow from a repository finding to an adopted plan. Review [master and open work](planning-preflight.md) before adopting packages.

Use **Projects → Inspect → Configure AI providers** to route operations to enabled profiles. In **Capacity**, review attributed usage, Factory-local project limits and any account windows reported by the provider. Follow [provider routing and budgets](providers.md) for configuration and reconciliation.

## Browser validation

GitHub Actions runs the [Chromium browser suite](../tests/mission-control/README.md) against a local Factory controller. It covers sidebar authentication and navigation, project review through plan adoption, rejection of a stale plan, and reconciliation of linked budget reservations after a host change.

## Authentication and safety

The browser holds the controller API token in session storage for the tab. The controller authenticates administrator requests and enforces worker execution policy. Repository permissions and branch protection govern GitHub integration. Project-scoped RBAC remains separate work.
