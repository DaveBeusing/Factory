# LT-04 operator authorization design

Status is proposed. The starting point is `master` at `ee922ba`, with LT-03 recovery tests passing. The dashboard currently accepts the administrator bearer for its operator routes. Remote workers and the executor have separate credentials. Worker project grants already constrain execution. Human project authorization will add a separate identity and access model to the dashboard.

## Roles and permissions

The initial grants are per human subject and project. A subject may hold multiple roles within the same project. The API resolves membership on the server for every request.

| Role | Proposed permission |
| --- | --- |
| Viewer | Read the project's goals, jobs, review summaries, usage totals and redacted execution evidence. |
| Planner | Viewer access plus draft goals, saved plans, project review and reviewed plan adoption. Provider-backed planning must continue to respect the existing budget gates. |
| Operator | Viewer access plus project-scoped pause, resume and stop, and reviewed execution recovery. Global queue actions and worker provisioning remain administrative. |
| Budget reconciler | Viewer access plus evidence-backed provider and goal budget settlement and explicit overrun confirmation. Recovery that posts usage requires this permission. |
| Administrator | Manage human identities, grants, credentials and controller-wide settings. Existing single-operator installations keep their administrative entry point during migration. |

Register each human with a stable server-assigned subject ID. Initially issue a separate revocable credential with only a verifier stored by Factory. Never accept a claimed subject or role from the request body. Keep administrator, operator, worker and executor credentials in separate authentication paths. Record who granted or revoked each role. Document a local administrator recovery path before enabling human access in a shared installation.

## API boundaries

| Current surface | LT-04 handling |
| --- | --- |
| `/api/state`, `/api/providers`, `/api/usage`, `/api/health`, `/api/settings` and `/api/workers/managed` | Keep administrative access. Add explicit project projections for operators. Filter each aggregate by project before computing totals. Account quotas, host details and global worker lists remain administrative. |
| `/api/events/stream` | Keep the existing stream administrative. Add a project stream only after event ownership and revocation during a connection are covered. Unscoped events stay out of project streams. |
| `/api/projects/{id}/analysis` and project settings | Resolve the project on the server. Allow the project-specific analysis actions by role. Keep provider routing and controller-wide configuration administrative in the first release. |
| `/api/plans/{goalId}`, `/api/goals/{id}/*` and `/api/jobs/{id}/*` | Resolve ownership through the stored goal or job. Authorize the specific operation and verify all referenced IDs, including dependencies, reservations and provider turns. Keep cross-project moves administrative. |
| `/api/providers/budget-reservations/{id}/*` and receipt recovery | Resolve reservation → usage → project and receipt turn → lease → job → project. Expose a summary and recovery action. Keep raw receipts, grant digests and lease credentials inaccessible to project operators. |
| `/api/queue/*`, `/api/workers/*`, `/api/worker/*`, `/api/executor/*` | Keep global scheduling and worker administration restricted. Preserve the dedicated worker and executor authentication and authorization contracts. |

An unregistered operator route is denied. Return HTTP 401 for invalid credentials. Return HTTP 404 for out-of-scope or nonexistent resources so callers cannot discover other projects by guessing IDs. Return HTTP 403 for a valid project member lacking the specific operation. Recheck membership and resource ownership in the transaction that persists sensitive changes. For long-running actions, recheck before recording the result and fence or discard output if authorization was revoked.

Persist the server-derived actor, project, target, operation and timestamp with approved decisions. Scope amendments, adoption, recovery, budget settlement and overrun confirmation also carry the operator's recorded evidence. The audit record must commit with the mutation. Define audit retention explicitly before relying on the bounded operational event feed for investigations.

## Implementation order and acceptance

1. **Project reads.** Add the human credential and project grant records, then expose a minimal project summary and redacted job evidence. Cover direct API access and deny all other operator routes. This slice can ship before scoped writes and streams are enabled.
2. **Project mutations and audit.** Add planning, control and reconciliation permissions in that order. Bind the actor to each audited mutation. Cover revoked grants, mismatched body IDs and transaction rollback.
3. **Mission Control and operational migration.** Switch operator views to the explicit scoped routes. Add project streams only with proven ownership and revocation. Rehearse upgrading an administrator-only installation, recovering administrator access and rotating a human credential.

For every slice, test two projects and at least two operator identities. Include an allowed request for the intended project, the same request for the other project, foreign child IDs in a permitted request, and a revoked credential or grant. Verify that operator tokens cannot call administrative, worker or executor routes. Use Chromium to confirm that allowed actions work and denied actions display a clear result. Direct API tests remain the authority for access control.
