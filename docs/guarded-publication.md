# Guarded publication

[← Documentation](README.md) · [Mission Control](mission-control.md#decisions-workspace) · [Quality gates](quality.md)

## Authorize an operation

Submit a `SignedPullRequestDecision` to the administrator endpoint `POST /api/jobs/{id}/publication`. The integration authority signs the UTF-8 bytes of `Json.Stringify(PullRequestDecision)` with RSA-PSS SHA-256 and supplies the Base64 signature. Configure the authority's PEM public key in `FACTORY_INTEGRATION_PUBLIC_KEY`. The decision binds the operation to the job, project and scope revisions, repository, exact base and head commits, GitHub account, method and expiry. The controller checks that its GitHub credential matches the signed account and has repository write permission.

All actions require `allowPush`. A current [signed independent review](quality.md) is required for draft, check and merge decisions.

| Action | Effect |
| --- | --- |
| `draft` | Creates a draft from an already published Factory branch at the approved head. The branch must be locked against updates and deletion without bypass. For PR-triggered CI, use method `pr-ci` and pull number zero. |
| `check` | Records readiness for an existing PR on the signed head and base after checking strict repository rules, scope, exact workflow evidence and independent GitHub approval. The PR must be open, clean and out of draft. Use a non-admin repository credential. |
| `merge` | Verifies a GitHub merge that has already happened, with `autoMerge` enabled. GitHub cannot enforce the signed expected base atomically at merge time, so the controller refuses to perform the merge itself. |
| `reverify` | Checks a historical hosted integration against the current project target using a signed decision for the already merged PR. |

For draft creation, the signed `branchChecksAvailable` field selects the check path. With branch checks, the controller verifies required runs and their configured GitHub Apps. The PR-only path requires trusted hosted evidence that the workflow runs only on pull requests and that the required context is pinned to GitHub Actions.

A durable reservation precedes draft creation. If the request ends with an uncertain outcome, inspect the PR on GitHub and the controller audit before proceeding. The coordinator refuses to replay a reserved mutation.

Perform the merge through the repository's authorized GitHub process. The controller reconciles ordinary merges against both signed parents. Supported squash and rebase merges must preserve the reviewed tree and descend from the signed base. The controller confirms the remote integration and synchronizes a clean local base before recording verification.

## Required workflow evidence

For readiness and hosted merge verification, the selected PR workflow attempt must have passed on the authorized head and base. Factory pins required checks to the GitHub Actions app and matches their exact check-run identities to jobs from that attempt. It also checks for approval on the exact head from a GitHub reviewer with repository write access who is distinct from the PR author and the signed account.

When GitHub supplies the PR association on the run, Factory validates it directly. If the run omits it, Factory looks for a signed event receipt in the decision or in stored webhook deliveries. If no receipt is available and the signed decision includes `workflowFile`, Factory verifies an Actions artifact attestation.

- **Actions attestation.** Factory's validation workflow attests a compact `pr-event.json` receipt on same-repository pull requests. The verifier requires a GitHub-hosted runner, the specified signer workflow, the exact run and attempt, and the PR merge ref. It checks the attested merge commit's base and head parents and matches the workflow file across the base, head and attested merge commit.
- **Signed event receipt.** Configure the event authority's PEM verification key in `FACTORY_WORKFLOW_EVENT_PUBLIC_KEY`. The authority signs the UTF-8 bytes of `Json.Stringify(WorkflowEvent)` with RSA-PSS SHA-256. The receipt and Base64 signature bind the repository, workflow, run attempt, suite, PR number, base branch, base commit and head commit. The authority must derive those fields from authenticated attempt-specific evidence.
- **Webhook receiver.** Subscribe to GitHub repository `workflow_run` deliveries at `POST /api/github/workflow-events` on a reachable HTTPS controller. Configure the shared `FACTORY_GITHUB_WEBHOOK_SECRET` and the issuer's PEM `FACTORY_WORKFLOW_EVENT_PRIVATE_KEY`. The endpoint authenticates raw delivery bytes using `X-Hub-Signature-256` and retains signed receipts for registered repository and base pairs. Duplicate receipts are idempotent. A delivery must contain one authoritative PR association.
