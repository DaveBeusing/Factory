# Historical work-package archive

[← Documentation](../README.md) · [Planning preflight](../planning-preflight.md) · [Roadmap](../roadmap.md)

> [!IMPORTANT]
> These files preserve **scope, risks and delivery evidence as written at the time**. Their original draft/partial status lines do not describe the live GitHub state. Always inspect the current `master` source, open PRs and required checks before deciding whether an implementation is complete or a new package is needed.

| Work package | Historical focus | Canonical current guide |
| --- | --- | --- |
| [WP-01: Dynamic goal budget allocations](wp01-local-budget-extensions.md) | Controller-approved token grants and accounting | [Provider budgets](../providers.md) |
| [WP-02: Focused SQLite state transactions](wp02-granular-state-transactions.md) | Transactional persistence and recovery | [Architecture](../architecture.md) · [Configuration](../configuration.md) |
| [WP-03: Mission Control inspectors](wp03-operational-inspectors.md) | Job detail, evidence and recovery actions | [Mission Control](../mission-control.md) |
| [WP-04: Master-first preflight](wp04-master-first-preflight.md) | Existing-work inspection and adoption | [Planning preflight](../planning-preflight.md) |

**Archive rule:** preserve original contracts and findings; put current operating instructions in the guides listed above. A new PR should reference its own verified base SHA and exact check results rather than inheriting status from an archival package.
