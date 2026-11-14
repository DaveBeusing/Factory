# Factory command reference

**Commands verified against the .NET 10 CLI source on 2026-11-10.**

[← Documentation](README.md) · [Getting started](getting-started.md) · [Recovery](recovery.md)

```powershell
.\dist\Factory.exe --help
.\dist\Factory.exe version
.\dist\Factory.exe doctor
```

> [!NOTE]
> Commands below use the generated Windows executable. `Build.ps1` must have produced `dist/Factory.exe` first. Some commands require a configured `FACTORY_HOME`, registered project, authenticated native provider or controller access. Bracketed arguments in help text are optional; the examples use placeholders that you must replace.

## Project and planning workflow

| Command | Purpose |
| --- | --- |
| `init <repo> --name NAME [--base master] [--checks-json PATH]` | Register a repository; Factory enforces the `master` base |
| `goal add --project NAME --objective TEXT [--criteria TEXT] [--budget TOKENS]` | Create a goal |
| `plan --goal GUID` | Inspect master/open work and propose a plan |
| `adopt --goal GUID --plan-revision GUID --reviewed-existing-work [--mode local\|remote]` | Adopt the reviewed revision |
| `enqueue --project NAME --title TITLE --description TEXT [--paths file1,file2] --reviewed-existing-work` | Direct reviewed job; nonempty scope required |
| `run` | Run the eligible job queue |
| `status` | Display current projects, goals, jobs and workers |

Planning and execution are separate permissions and transitions. See [Master-first preflight](planning-preflight.md) and [Quality checks](quality.md). For direct jobs, `--mode local|remote`, priority, risk and dependencies may be specified where supported.

## Execution and recovery

| Command | Effect |
| --- | --- |
| `pause` / `resume` | Pause or resume new queue admissions; **not** guaranteed process termination |
| `recover` | Quarantine expired leases; inspect before retry |
| `reconcile <job-guid> --reviewed --reason TEXT` | Human-reviewed local job requeue eligibility |
| `verify <job-guid>` | Confirm actual integration in the intended base |
| `worker-token --id WORKER_ID` | Generate a worker-scoped token on the controller |
| `worker --server HTTPS_URL --workspace DIR --id WORKER_ID` | Start a remote worker using `FACTORY_WORKER_TOKEN` |
| `dashboard [--port 8787]` | Start Mission Control; `--listen URL` is also parsed |

Stop and recovery actions may leave worktrees and provider holds to reconcile. See [Recovery](recovery.md) and [Remote workers](remote-workers.md).

## Analysis and provider operations

| Command | Purpose |
| --- | --- |
| `providers` | Configured providers, routes and observed token usage |
| `usage` | Provider account allowance observations, when available |
| `analytics` | Execution metrics |
| `graph --project NAME` | Build heuristic source relationship index |
| `impact --project NAME --file RELATIVE_PATH` | Inspect likely affected symbols/imports |
| `knowledge --project NAME [--topic TOPIC --content TEXT]` | Read/store project knowledge |
| `eval --suite FILE` | Run evaluation cases; project context may be selected for supported agent cases |
| `test-audit --project NAME [--enqueue --reviewed-existing-work]` | Evaluate test gaps; reviewed flag is required for queued actions |
| `install-skills [--overwrite]` | Install bundled Codex assets into its native locations |

Account allowance should not be inferred from local token consumption. Graph/impact analysis is heuristic. See [Providers](providers.md).

## Project-scoped human access

These are **controller operator commands**, not job execution commands:

```text
viewer create --name NAME
viewer rotate|disable --subject GUID
viewer grant|revoke --subject GUID --project NAME
viewer grant-planner|revoke-planner --subject GUID --project NAME
viewer grant-operator|revoke-operator --subject GUID --project NAME
viewer grant-budget-reconciler|revoke-budget-reconciler --subject GUID --project NAME
```

A subject's token is distinct from the administrator token and from worker/executor credentials. The controller enforces project ownership on the server. [Operator authorization](operator-authorization.md) documents scoped operations and denied paths.

## Release preparation

```powershell
.\dist\Factory.exe release --project 'my-project' --version '0.3.0'
dotnet run --project tools/Factory.Versioning -- build-version
```

`release` prepares local files and metadata; it does **not** publish, tag, sign, merge or deploy. Git-derived versioning is separate from the release helper. See [Versioning](versioning.md).

**Next:** [Architecture](architecture.md) · [Full documentation index](README.md)

## GitHub issue intake

Run `issue-intake --project NAME` from the local operator CLI. It uses the registered GitHub origin and the paginated GitHub inventory through the planning preflight adapter. Incomplete scans never create a goal. Bot, app, and authenticated self-authored issues are held. The default policy holds observations. Administrators can read and update `/api/projects/{projectId}/issue-intake-policy` with the current `expectedRevision` and a rule of `hold`, `deny`, `escalate` or `accept`. Acceptance creates one linked draft goal per issue. Policy changes immediately invalidate old intake acceptance. A successful rescan is required before an accepted draft can be adopted. A closed or missing issue loses its acceptance on rescan. Drafts are retained for review. If the source or intake policy revision changes, save and review a new plan revision before adoption. Intake does not run jobs or write to GitHub.
# Versioned specifications

Plan save endpoints accept `contractVersion: 1`. Each package declares owned files, package dependencies, and an `acceptanceEvidence` array with a criterion and the name of a configured project check. An optional `integratedPrerequisites` array identifies a same-project job, its `headCommit`, and the verified `integratedCommit`. The controller checks these references at save and adoption. Overlapping package paths require explicit dependency ordering.

Each saved revision returns its previous revision and package changes. Edit scope, risk, security requirements, or ownership by submitting a new draft revision, then obtain the policy-required approval for that revision. Existing unversioned plans remain readable and can be converted by filling in the contract fields and saving as version 1. Historical records are retained. Adopted version 1 contracts require a separately reviewed replacement plan for contract changes.

## Shared scheduling resources

A plan package can specify sharedResources using kind:name keys. Supported kinds are interface, schema, ci, config and generated. Specify an empty array only when the package shares none of these resources. Unknown ownership serializes work within the same project. The scheduler holds resources through the execution lease in both local and remote claims. Administrators can inspect local queue decisions at GET /api/scheduler/decisions.
A fenced job that cannot safely be requeued can release its resources through the administrator endpoint `POST /api/jobs/{id}/release-resources`. Send the job's current `controlRevision`, `runnerStopped: true`, `gitEffectsReviewed: true` and a reason. Establish remote or former-host process termination independently and reconcile branch, push, and pull-request effects before confirming. Existing budget holds must be settled separately. The endpoint records the decision and leaves the old job cancelled.
