# Mission Control navigation and connection

Mission Control is a browser-based operations workspace embedded in the .NET executable. It does not require Node.js or npm.

## Connection controls

The **F** brand symbol in the sidebar opens the connection-settings dialog. Its small indicator is derived from observed controller event-stream health:

| Indicator | Observed meaning |
| --- | --- |
| Green with a gentle irregular pulse | Healthy recent heartbeat |
| Amber | Stream is connecting or heartbeat is delayed |
| Red | Offline, invalid credentials or an expired heartbeat |

The pulse honors the operating system's reduced-motion preference. The sidebar connection text and footer are also kept up-to-date so status is never represented by color alone.

## Topbar actions

An icon-only **+** button is immediately left of Search and opens creation shortcuts for projects, goals, jobs and workers. Search opens the command palette; Ctrl+K and Cmd+K also open it.

## Planning and operations

Use **Project review** to create a source-commit-bound review, inspect evidence-backed findings, and create editable draft goals. Use **Goal planner** to filter by project, inspect live preflight evidence, select dependencies by title, review budgets and adopt packages explicitly. Use **Projects → Inspect → Configure AI providers** to route operations to enabled provider profiles. The **Capacity** page displays actual provider/model/role usage and Factory-local project limits separately from genuinely reported account allowances.

See [Project review](project-reviews.md), [Provider routing and budgets](providers.md) and the [root README](../README.md) for workflow instructions and limitations.

## Authentication and safety

The browser holds the controller API token in session storage for the tab. The UI must not be regarded as an authorization boundary. Repository push, merge, deployment, worker execution rights, secrets and role-specific permissions remain controlled by server-side policies. Verified team-grade project-scoped RBAC and browser end-to-end validation are separate work.
