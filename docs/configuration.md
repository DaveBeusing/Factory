# Configuration, local state and credentials

**Know what Factory stores, how upgrades work, and which credentials belong on which host.**

[← Documentation](README.md) · [Getting started](getting-started.md) · [Remote workers](remote-workers.md)

## Storage locations

By default on Windows, Factory uses `%USERPROFILE%\.factory\`. Override the root for the **current process** with:

```powershell
$env:FACTORY_HOME = 'C:\FactoryState'
```

| Location below `FACTORY_HOME` | Purpose | Handling |
| --- | --- | --- |
| `settings.json` | Runtime limits, provider profiles, API secrets and execution settings | **Sensitive** |
| `factory.sqlite3` | Relational projects, goals, plans, jobs, reservations, events and authority records | **Back up** |
| `usage.json` | Cached Codex account observations when supplied | May be missing or stale |
| `plugins.json` | Explicitly configured .NET plugin assemblies | **Trusted code only** |
| `worktrees/` | Isolated job Git worktrees | Preserve on interruption |
| `evidence/` | Check and review records tied to source commits | Preserve with job history |
| `plans/` | Historical fallback plan files | Not the primary persisted plan revision store |

> [!WARNING]
> Do not commit `.factory/`, secret-bearing settings, database files or runtime logs to Git. `FACTORY_HOME` is **application state**, not a project configuration convention to check into target repositories.

## SQLite and upgrade policy

The inspected `master` code sets `StateStore.DatabaseVersion = 18`. This is the **SQLite schema version**; it is separate from product SemVer and from the serialized `FactoryState.Schema` property.

- Current storage uses relational tables with foreign keys, WAL, indexes and transactional mutations, not the former single JSON row.
- The code provides upgrade steps for supported earlier **relational** schema versions (including 2–17). Upgrade coverage differs by historical schema; test the exact source/target pair you operate.
- **Legacy single-row `factory_state` JSON databases are not imported.** A nonempty unsupported database is rejected instead of silently overwritten.
- Downgrading a binary is not an inverse migration. Restore a verified backup created for the previous version.

### Before upgrading or moving state

1. Pause admission, then **stop** the controller and all worker/executor processes. Pausing admission alone does not stop active work.
2. Preserve the entire `FACTORY_HOME`, including SQLite and, after stopping, any WAL/SHM files, settings, evidence and worktrees.
3. Preserve the matching executable version and `dist/build-manifest.json`. For remote execution, back up the executor's dispatch and receipt directories consistently.
4. Rehearse the restore with an isolated `FACTORY_HOME` and no live workers before touching the operational installation.
5. Start the intended binary, inspect its migration and state output, and confirm project/job/usage histories before resuming claims.

See [Recovery](recovery.md#restore-or-upgrade-state) and [Versioning compatibility evidence](versioning.md#lt-07-platform-and-upgrade-matrix). Historical test fixtures do not certify all customer upgrade paths.

## Runtime settings

Common fields in `settings.json`:

| Setting | Meaning | Typical default |
| --- | --- | --- |
| `maxParallel` | Global simultaneous jobs | `3` |
| `maxAgentSessions` | Concurrent native agent sessions | `6` |
| `maxReviewRounds` | Bounded validation/rework rounds | `3` |
| `leaseSeconds` | Worker lease duration | `120` |
| `runtime` | Codex adapter mode: `exec` or `app-server` | `exec` |
| `model`, `modelRoutes` | Provider/model selection | Provider defaults |
| `allowPush` | Permission to publish a branch | `false` |
| `autoMerge` | Reserved compatibility setting; no automatic merge implementation | `false` |
| `workspaceRoot` | Worktree storage location | Inside `FACTORY_HOME` |
| `apiToken` | Controller administrator bearer credential | Locally generated |
| `workerTokenKey` | Controller secret used for worker-specific credentials | Locally generated |

Edit settings through authenticated Mission Control where supported or update the file with the controller stopped. Preserve existing tokens when editing provider profiles. Running processes may need restarting to pick up environment or configuration changes.

## Credential boundaries

| Credential | May access | Must not be shared with |
| --- | --- | --- |
| `apiToken` | Administrative controller APIs | Workers, untrusted clients and PRs |
| `workerTokenKey` | Derives each worker's separate credential | Remote workers or browsers |
| `FACTORY_WORKER_TOKEN` | One worker identity (`X-Factory-Worker-Id`) | Other workers or administrator APIs |
| Project viewer token | Authorized project read/plan/operate/reconcile routes according to grants | Global controller/worker/executor APIs |
| Remote executor token | Executor-specific communication and receipt paths | Browser/admin consumers |

Human project grants are distinct from worker project grants. The implemented project-scoped Viewer/Planner/Operator/Budget Reconciler paths are **not yet a complete identity provider or enterprise RBAC implementation**. See [Operator authorization](operator-authorization.md).

## Useful environment overrides

| Variable | Purpose |
| --- | --- |
| `FACTORY_HOME` | Location of controller state |
| `FACTORY_AGENT_EXECUTABLE` | Explicit native Codex executable path |
| `FACTORY_CLAUDE_EXECUTABLE` | Explicit native Claude Code CLI path |
| `FACTORY_WORKER_TOKEN` | Worker-scoped credential for `Factory worker` |
| `FACTORY_REMOTE_EXECUTOR_TOKEN` | Executor credential where configured for mixed remote turns |

Never print environment dumps containing credentials in logs or diagnostics.

## Quotas, extensions and plugins

Factory-local token ceilings are **not** provider-account subscriptions or credits. Show account windows only when actually supplied; a cached or expired observation must be labeled accordingly. The [provider guide](providers.md) defines the ledger, reservation and reconciliation semantics.

.NET plugins are loaded into Factory's process without sandbox isolation. Install only intentionally trusted assemblies. Local Git worktrees and container-based **checks** are not proof that an agent itself is OS-isolated.

**Next:** [Remote security and worker grants](remote-workers.md) · [Recovery](recovery.md)
