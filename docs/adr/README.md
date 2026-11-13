# Architecture decision records

[← Documentation](../README.md) · [Architecture](../architecture.md)

> [!NOTE]
> **Decision status is distinct from implementation.** A proposal can have working code while remaining labeled *proposed*. Do not infer maintainer acceptance from merged commits or test results alone.

| Record | Recorded status | Scope |
| --- | --- | --- |
| [ADR-0001: Git-derived preview versioning](0001-versioning.md) | **Accepted for implementation** | SemVer, MSBuild baseline, preview revision provenance |
| [ADR-0002: Role-scoped provider routing](0002-provider-routing.md) | **Proposed for maintainer review** | Provider routing, budget reservations, role restrictions |

## How to use these records

- [ADR 0003 · Policy-governed intake](0003-policy-governed-intake.md)
- [ADR 0004 · Specification approval](0004-specification-approval.md)
- [ADR 0005 · Independent review](0005-independent-review.md)
- [ADR 0006 · Policy-controlled integration](0006-policy-controlled-integration.md)

1. Read the decision's original context, alternatives and consequences.
2. Verify the actual code and test coverage before describing a mechanism as implemented.
3. Document a new proposal or explicit amendment when changing a foundational choice.
4. Record approval separately from deployment and release readiness.

For the operating state, refer to [Architecture](../architecture.md), [Providers](../providers.md) and [Versioning](../versioning.md).
