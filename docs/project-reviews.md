# Project review and goal planning

1. In Mission Control, open **Project review** and select a repository.
2. Select **Analyze project**. Factory reads the committed `master` tree and takes a snapshot of configured quality gates and blocked or failed jobs.
3. Read the findings. The test-directory and README checks use committed paths. The test-directory heuristic provides a starting point for checking test discovery.
4. Select **Review & create draft goal** on a finding. Edit the objective, acceptance criteria and total token budget before saving.
5. Continue in the project-filtered **Goal planner**. Use **Goal & budget** to inspect provider usage and available capacity. Use **Check master & open work** to see matching source, jobs, PRs and issues before generating or editing packages.
6. Select package dependencies by title. Review the saved plan's source, package paths and overlap evidence before choosing **Adopt reviewed plan**. Adoption requires explicit acknowledgement. See [master-first preflight](planning-preflight.md).

The default review inspects committed paths and Factory state. If the project assigns an **analysis** provider, Factory also requests advisory commentary using a bounded path list and the deterministic findings. Advisory mode requires a clean checkout at the reviewed master commit and uses the selected provider's local budget. The advisory is displayed separately so operators can distinguish model observations from verified findings. For provider configuration and capacity semantics, see [provider routing and budgets](providers.md).

Factory stores the latest review per project in `project-reviews/<project-id>.json` under the Factory data directory. Creating a goal checks that master still matches the reviewed commit. Mission Control also sends the review ID so a form opened before a replacement review cannot use the newer snapshot by accident.

The administrator API exposes `GET/POST /api/projects/{id}/analysis` and `POST /api/projects/{id}/analysis/goals`. Goal creation accepts `reviewId`, `findingId`, edited objective and acceptance criteria, and a positive token budget. The returned goal is a draft.
