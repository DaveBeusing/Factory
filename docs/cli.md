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

Run `issue-intake --project NAME` from the local operator CLI. It uses the registered GitHub origin and the same bounded issue inventory as planning preflight. Incomplete scans never create a goal. The default policy holds observations. Administrators can read and update `/api/projects/{projectId}/issue-intake-policy` with the current `expectedRevision` and a rule of `hold`, `deny`, `escalate` or `accept`. Acceptance creates one linked draft goal per issue. A policy change applies on rescan and does not delete an existing draft. Intake does not run jobs or write to GitHub.
