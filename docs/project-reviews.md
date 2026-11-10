# Project review and goal planning

**Turn inspectable repository findings into a draft goal and an explicitly reviewed plan.**

[← Documentation](README.md) · [Mission Control](mission-control.md) · [Preflight](planning-preflight.md)

> [!IMPORTANT]
> Project review is **source- and state-informed**, not an autonomous implementation decision. A deterministic finding or model commentary must still be checked against the repository and related open work. Plan adoption is a separate explicit action.

## From a finding to an adopted plan

1. **Select the project.** In Mission Control, open **Project review** and choose a registered repository.
2. **Analyze committed master.** Select **Analyze project**. Factory reads the reviewed master commit and a snapshot of configured quality gates and blocked/failed jobs.
3. **Inspect the evidence.** Read findings and affected files. Test-directory and README heuristics operate on committed paths; a matching path does not prove test discovery or sufficient coverage.
4. **Create a draft goal.** Choose **Review & create draft goal**, then refine the objective, acceptance criteria and total token budget. The goal remains a draft.
5. **Open Goal planner.** Select the project-filtered goal, inspect **Goal & budget**, and use **Check master & open work** to review source candidates, existing Factory jobs and available open GitHub PRs/issues.
6. **Build the plan.** Add named work packages with expected paths, dependencies, validation needs and sensible risk boundaries. Resolve overlaps before planning parallel execution.
7. **Review the saved revision.** Confirm source SHA, project ownership, package scope, real checks and related work. Choose **Adopt reviewed plan** only after the human preflight review; the dialog asks for an explicit acknowledgement and execution mode.
8. **Run and verify.** Execution is separate from adoption; follow [Getting started](getting-started.md#plan-run-and-verify) and [Quality](quality.md). Merge manually and verify integration.

## Deterministic review versus advisory commentary

| Source | What it establishes | What it does not establish |
| --- | --- | --- |
| Committed master paths | Presence of candidate files and basic project signals | Semantic correctness or complete coverage |
| Configured checks and current Factory jobs | Existing declared quality gates and recorded statuses | That checks passed for every unobserved revision |
| Optional assigned analysis provider | Advisory commentary over bounded master context and deterministic findings | Verified source truth, authorization, or a new budget allowance |

An advisory provider requires a clean checkout at the reviewed master commit and spends its configured **local** budget. The UI presents advisory commentary separately from deterministic findings. See [Provider routing](providers.md).

## Revision safety and storage

Factory stores the latest review per project in `project-reviews/<project-id>.json` below `FACTORY_HOME`. Goal creation checks that master still matches the reviewed commit; the UI also passes the review ID so an open form cannot silently adopt findings from a newer review.

Plan adoption similarly requires the exact reviewed plan revision. If master advances or previously verified open-work evidence changes, refresh the preflight rather than adopting stale work.

> [!TIP]
> Finish, repair and verify an incomplete existing implementation before proposing a parallel replacement. Scope conflicts and open PR overlap are coordination problems, not invitations to regenerate the same feature.

## API reference

The administrator controller exposes:

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/api/projects/{id}/analysis` | Read latest recorded analysis |
| `POST` | `/api/projects/{id}/analysis` | Analyze current master with the configured rules |
| `POST` | `/api/projects/{id}/analysis/goals` | Create a draft from a reviewed finding |

Goal creation accepts a review/finding identity, edited objective and criteria, and a positive budget. Project-token readers/planners use separately authorized [scoped routes](operator-authorization.md); they are not granted global administrator access.

**Next:** [Planning preflight](planning-preflight.md) · [Goal ledger and provider budgets](providers.md#goal-budget-and-job-reservations)
