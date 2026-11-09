# LT-04 operator authorization design

Mission Control supports project-scoped human credentials alongside administrator access. Remote workers and the executor use separate credentials. The Windows package validation exercises the migration from a synthetic administrator-only installation, local administrator recovery and live project-token rotation.

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
| `/api/events/stream` | Keep the existing stream administrative. `GET /api/viewer/projects/{projectId}/events/stream` streams redacted job events for a granted project and closes on credential or grant changes. Unscoped events stay out of the project stream. |
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

## Project read access

Run these commands from a trusted local administrator shell with access to the controller's `FACTORY_HOME`:

```text
factory viewer create --name "Project reader"
factory viewer grant --subject SUBJECT_GUID --project PROJECT_NAME
factory viewer revoke --subject SUBJECT_GUID --project PROJECT_NAME
factory viewer rotate --subject SUBJECT_GUID
factory viewer disable --subject SUBJECT_GUID
```

`create` returns a stable subject ID and the bearer token. It starts without project grants. Keep that token out of command logs and source control. `rotate` replaces the token while keeping the subject and its grants. `disable` invalidates the active token. The controller database stores the verifier and records access changes with the local OS account. The existing local administrator CLI remains available to recover access. After restoring an older controller backup, review grants and rotate affected viewer tokens before reopening the API, since the backup may predate a revocation.

Send the viewer token as an `Authorization: Bearer` header on these API requests.

- `GET /api/viewer/projects/{projectId}` returns the project summary, goal and job summaries, and recorded usage aggregated by provider within the project.
- `GET /api/viewer/projects/{projectId}/jobs/{jobId}/quality` returns quality status and the verification timestamp when available. The projection omits raw reports, free-text findings, check names, and host paths.

Invalid viewer credentials return HTTP 401. A valid viewer outside the project grant, or a request for another project's job, receives HTTP 404.

## Local administrator recovery and audit retention

The operator confirmed the manual Mission Control playtest on 2026-11-09 using the isolated two-project activity fixture. The Windows package rehearsal separately passed schema 8 to 18 migration, backup restore, administrator access recovery and project-token rotation on a running controller.

If the administrator token is lost or must be replaced, stop the controller. From a trusted local administrator shell, select its `FACTORY_HOME` and save a protected backup of `settings.json` and the controller database. Remove only the `apiToken` property from `settings.json`, then run `factory providers` locally to generate and persist a replacement. Restart the controller and reconnect with the new token from that file. The worker key and existing project grants stay in place. Treat the settings file and its backup as credentials.

To rotate a project token, run `factory viewer rotate --subject SUBJECT_GUID` from the same local shell. The replacement token keeps the subject and its grants. The previous token fails authentication as soon as rotation commits.

The controller retains the latest 20,000 operational events. Project Activity shows a redacted selection of the latest 100 job events for the selected project. Dedicated access, planning, job operation and budget audit tables live in the controller database and have no automatic age-based pruning. Include the database in backups and apply the installation's audit retention and access policy to those backups.

## Project planning

Use the same human credential with an explicit Planner grant. Schema 13 preserves Viewer grants and creates no Planner grants during upgrade.

```text
factory viewer grant-planner --subject SUBJECT_GUID --project PROJECT_NAME
factory viewer revoke-planner --subject SUBJECT_GUID --project PROJECT_NAME
```

A Planner grant includes project reads. Revoking it leaves any separate Viewer grant in place.

- `POST /api/viewer/projects/{projectId}/goals` creates a draft with `objective`, optional `acceptanceCriteria` and optional `tokenBudget`.
- `PUT /api/viewer/projects/{projectId}/goals/{goalId}` replaces the draft objective and criteria. Send the `goalRevision` returned by the goal's scoped plan read. A stale revision returns HTTP 409. The existing token budget is preserved. Drafts with work packages and goals outside the draft state cannot be edited here.

The route determines the project. If a body supplies a project ID, it must match the route. Cross-project requests return HTTP 404, and a Viewer attempting a write in a granted project receives HTTP 403. The controller checks the credential, grant and goal in the same SQLite write transaction that records the goal and the audit row. The audit stores the server-resolved subject, project, target, operation and timestamp.

### Saved revisions and adoption

These endpoints require a Planner grant for the goal's project.

- `GET /api/viewer/projects/{projectId}/plans/{goalId}` returns the current `goalRevision` and the latest `plan`, if available.
- `PUT /api/viewer/projects/{projectId}/plans/{goalId}` saves the supplied `packages`. Send the current `goalRevision` and the previously read plan `revision` when replacing a saved plan. The server computes the master preflight and returns the new revision.
- `POST /api/viewer/projects/{projectId}/plans/{goalId}/adopt` requires the exact `planRevision` reviewed, `reviewedExistingWork: true`, and a `mode` of `local` or `remote`. The server rechecks master and the applicable PR and issue inventory before creating jobs.

A stale goal, stale plan revision or changed master returns HTTP 409. Plan saves commit the revision and audit together. Adoption commits the jobs, goal state and audit together. The audit identifies the goal and exact plan revision, and records the review acknowledgment on adoption.

Schema 14 stores new plan revisions in SQLite. Existing plan files remain readable until a SQLite revision takes precedence. To adopt a legacy plan, save it to create a new SQLite revision and review that revision first. The same requirement applies to administrator and CLI adoption. Include the SQLite backup and any legacy plan files in controller backups.

`POST /api/viewer/projects/{projectId}/plans/{goalId}/generate` generates and saves a plan for an active project and a draft goal. Send the current `goalRevision` and the latest `revision` when replacing a saved plan. The controller selects the project's configured Planner provider and model. The invocation uses the existing goal, project and provider budget rules.

The controller binds generation to the original Planner grant and rechecks it before reserving tokens and again before publishing a revision. Revocation prevents publication even when a new grant is issued before the provider responds. The new grant authorizes a fresh request. Usage already incurred is still settled under the existing provider ledger rules. The reservation audit identifies the actor, project and ledger usage ID. A separate audit entry records any generated revision that was saved. Mission Control integration follows in a later increment.

## Project job controls

Schema 15 preserves existing grants and adds an independent Operator role. An Operator grant includes project reads.

```text
factory viewer grant-operator --subject SUBJECT_GUID --project PROJECT_NAME
factory viewer revoke-operator --subject SUBJECT_GUID --project PROJECT_NAME
```

`POST /api/viewer/projects/{projectId}/jobs/{jobId}/action` accepts `pause`, `stop`, or `resume` as the `action` for jobs without a lease. Resuming a previously started job requires `reviewed: true` and a `reason` that records the inspection. Active or held provider reservations for the job must be settled before resuming, including for jobs without a goal budget. The controller checks the grant, job ownership and applicable reservations in the write transaction. The same transaction records the actor, job, prior and resulting state, review acknowledgment, reason and timestamp.

The same action endpoint accepts `pause` and `stop` for a running local job with a current lease. It returns HTTP 202 and the requested state. The local runner observes the request at its next lease heartbeat or execution checkpoint. A run that has already finished can retain its final state. Interruptions during Git side effects use the existing quarantine path.

`GET /api/viewer/projects/{projectId}/jobs/operations` provides the checkpoint commit and its `recoveryRevision` for jobs eligible for local recovery. Send that revision with `reviewed: true` and a reason of 10 to 1000 characters to `POST /api/viewer/projects/{projectId}/jobs/{jobId}/reconcile`. A different checkpoint or execution generation returns HTTP 409 and requires a fresh review. The controller verifies the previous process and a clean worktree at a safe checkpoint, then rechecks the original Operator grant, job ownership and outstanding provider reservations before requeueing and auditing the decision. Goal-budget holds continue to block new claims until settled. Remote execution remains under its existing worker controls.

## Project provider budget reconciliation

Schema 16 adds an independent Budget Reconciler role that includes project reads.

```text
factory viewer grant-budget-reconciler --subject SUBJECT_GUID --project PROJECT_NAME
factory viewer revoke-budget-reconciler --subject SUBJECT_GUID --project PROJECT_NAME
```

`GET /api/viewer/projects/{projectId}/provider-reservations` lists held provider reservations available for scoped settlement. Send verified final totals to `POST /api/viewer/projects/{projectId}/provider-reservations/{reservationId}/reconcile` with `finalInputTokens`, `finalOutputTokens`, `reviewed: true`, and `reason`. Set `confirmOverrun: true` when verified usage exceeds the reservation.

The controller checks the grant and reservation ownership again in the write transaction. It settles the provider reservation, posts any linked job and goal usage delta, and records the actor, totals, evidence and overrun confirmation in the same transaction.

## Project goal budget settlement and executor receipt recovery

Schema 17 extends the Budget Reconciler grant with the following endpoints under `/api/viewer/projects/{projectId}`.

| Method and path | Purpose |
| --- | --- |
| `GET /goal-reservations` | List held goal reservations in the project. |
| `POST /goals/{goalId}/budget/reconcile` | Submit `reservationId`, `finalTokensIn`, `finalTokensOut`, `reviewed: true`, and `reason`. Set `confirmOverrun: true` for verified usage above the reservation. |
| `GET /executor-receipts` | List held executor turns eligible for investigation. |
| `POST /jobs/{jobId}/provider-turns/{turnId}/recover` | Submit `reviewed: true` and `reason` to recover final usage from the configured executor. Set `confirmOverrun: true` for a verified overrun. |

Settle linked provider holds before closing the goal hold. Where provider turns exist, the goal settlement must match the totals already booked to the job. The goal audit and release are committed together.

The controller authorizes the project and verifies the job, lease, turn and reservation links before asking the configured executor for the receipt. It rechecks the original grant and the execution generation before posting usage. Missing or invalid evidence leaves the hold in place. A successful response contains `turnId`, `reservationId`, and `state: "reconciled"`. Repeating a successful recovery returns the same result and leaves the ledger and audit unchanged.
