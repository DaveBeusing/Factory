# Factory documentation

**Operator procedures, implementation contracts and evidence, organized by task.**

[← Project README](../README.md) · [Quick start](getting-started.md) · [Architecture](architecture.md) · [Roadmap](roadmap.md)

> [!IMPORTANT]
> Factory is a development preview. The **running code and executable tests** establish current behavior. Planned architecture, historical validation and an accepted design decision do not, by themselves, establish that a feature works in every deployment.

## Choose your path

| Goal | Read first | Continue with |
| --- | --- | --- |
| **Install and launch** | [Getting started](getting-started.md) | [Configuration and storage](configuration.md) |
| **Understand the CLI** | [CLI reference](cli.md) | [Quality checks](quality.md) |
| **Use the dashboard** | [Mission Control](mission-control.md) | [Project review](project-reviews.md) |
| **Plan work safely** | [Master-first preflight](planning-preflight.md) | [Provider routing and budgets](providers.md) |
| **Configure workers** | [Remote workers](remote-workers.md) | [Operator authorization](operator-authorization.md) |
| **Handle interrupted work** | [Recovery](recovery.md) | [Capacity and provider holds](providers.md#budget-semantics) |
| **Assess implementation** | [Architecture and limitations](architecture.md) | [Roadmap](roadmap.md) |
| **Build a release** | [Versioning](versioning.md) | [Compatibility evidence](versioning.md#lt-07-platform-and-upgrade-matrix) |

## Operations

- **[Mission Control](mission-control.md):** navigation, connection status, scoped project views, keyboard actions, version display and recovery entry points.
- **[Project review and goal planning](project-reviews.md):** evaluate repository findings, create a goal, revise/adopt a plan.
- **[Master-first preflight](planning-preflight.md):** verify the exact base revision and inspect existing source, jobs and open GitHub work.
- **[Providers and budgets](providers.md):** Codex, Claude Code, Ollama, role restrictions, observed tokens, reservations, and remote routing.
- **[Remote workers](remote-workers.md):** separate credentials, project grants, capability attestations and required GitHub checks.
- **[Operator authorization](operator-authorization.md):** project-scoped viewer/planner/operator/reconciler permissions and server-side API boundaries.
- **[Recovery](recovery.md):** expired leases, receipt recovery, held usage, state backups and reconciliation.

## Engineering and validation

| Reference | Purpose |
| --- | --- |
| [Architecture](architecture.md) | Source boundaries, orchestration and implemented versus planned capabilities |
| [Quality gates](quality.md) | Check JSON, scope enforcement, evidence and review behavior |
| [CLI reference](cli.md) | Verified command groups and syntax |
| [Mission Control browser tests](../tests/mission-control/README.md) | Local Chromium test workflow |
| [Versioning and release governance](versioning.md) | SemVer and Git-derived previews, provenance, platform/upgrade evidence |
| [Queue and SQLite load baseline](queue-load-baseline.md) | Historical LT-06 measurement and provisional warning thresholds |
| [Long-term roadmap](roadmap.md) | Milestones, dated evidence and explicit open gates |

## Decisions and history

- **[Architecture decision records](adr/README.md):** accepted and proposed decisions are labeled separately; implementation does not automatically approve an ADR.
- **[Work-package archive](workpackages/README.md):** original scope, delivery claims and historical review status. Do not treat a past draft status as the live GitHub state.

> [!TIP]
> Keep new technical facts in the **most specific canonical guide**, then link to that guide from the README and relevant workspaces. Avoid copying long, potentially divergent procedures into multiple files.
