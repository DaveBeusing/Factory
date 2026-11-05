# Project reviews and goal planning

Mission Control provides a project-specific **Project Review** workspace. Reviews are provider-generated **proposals**, not certified security findings, test coverage reports, or approval decisions.

## Workflow

1. Open **Projects**, choose a repository, or open **Project Review** from the navigation.
2. Select the project and click **Start read-only review**. Factory captures the repository commit and a bounded code index, configured check names, and recent recorded job observations.
3. The configured `analysis` provider receives a read-only review request. Its reported tokens count against the project/provider budget; no automatic source modifications are authorized.
4. Inspect the returned summary, severity, evidence path, suggested objective and acceptance criteria for each finding. A verified evidence path means that the named file exists **inside the repository**; it does not verify the claim itself.
5. Choose **Prepare goal draft** to open the ordinary Goal form with the proposed objective and acceptance criteria prefilled. Edit them and explicitly save the goal.
6. Use **Plans** to create or generate work packages, review dependencies and parallelization, then inspect goal and provider budget capacity before adoption.
7. Adopt only after reviewing the plan and required authorizations. Jobs still follow the existing approval, quality and integration process.

The report is preserved outside the source repository under Factory's local `.factory/project-reviews/` state, with source commit, provider, time and unverified findings. A later repository revision does not retroactively validate a previous report. To see updated findings, start a new review.

Project reviews do not automatically create goals, authorize work, modify provider security policy, open PRs, or merge changes. Findings requiring independent evidence must be checked by a reviewer. An unavailable CLI, unsupported provider capability or exhausted local token budget blocks the operation instead of inventing results.

See [AI provider routing and budgets](providers-and-budgets.md) for provider eligibility and accounting, and [README](../README.md) for the supported runtime.
