# Project review and goal creation

Mission Control's **Project review** view provides a read-only, commit-bound analysis workflow:

1. Select a registered project.
2. Run **Analyze project** to create an immutable-at-source snapshot.
3. Inspect findings, severity, evidence and suggested outcomes.
4. Use **Review & create draft goal** to adjust the outcome, acceptance criteria and token budget before creating the goal.
5. Continue in **Goals → Plans** to propose or edit work packages and dependencies.
6. Review the local goal/provider budgets and explicitly adopt an eligible plan.

The deterministic inspection currently checks configured quality gates, a heuristic for a root test directory, root README presence and the controller's blocked/failed project jobs. It builds a bounded source index and associates results with the observed Git HEAD. These checks are **not** a comprehensive source-code audit, vulnerability scan, coverage analysis or independent model review. An absence of findings is not proof of quality.

Reviews are persisted under the user's Factory data directory, `.factory/project-reviews/<project-id>.json`, never inside the target repository. Goal creation rejects stale snapshots whose source HEAD has advanced, and a generated goal starts in `draft` state. A finding cannot independently authorize plan adoption, job execution, privileged actions, Git push, merging or deployment.

API endpoints: `GET/POST /api/projects/{id}/analysis` and `POST /api/projects/{id}/analysis/goals`. The latter accepts a finding ID, optional edited objective/acceptance criteria and a positive token budget. These endpoints are controller-admin operations under the current token authorization model. Project-based user RBAC is not yet implemented.

Future richer project analysis can integrate read-only provider routing, verified quality evidence and issue host metadata. Until that work is implemented, findings must be described as deterministic observations rather than model-certified conclusions.
