# Factory engineering documentation

Start with the [root README](../README.md) for installation, build and CLI commands.

## Operator guides

- [Mission Control](mission-control.md) covers dashboard navigation, connection health and operator controls.
- [Project review and goal planning](project-reviews.md) follows a repository finding through goal creation and plan adoption.
- [Master-first preflight](planning-preflight.md) explains how to review source, jobs, pull requests and issues before adoption.
- [Provider routing and budgets](providers.md) covers adapter capabilities, role assignments, goal and provider limits, and usage reporting.
- [Recovery](recovery.md) covers expired leases, same-host and cross-host recovery, and settlement of held provider and goal reservations.
- [LT-04 operator authorization design](operator-authorization.md) describes the proposed project roles, API boundaries and acceptance tests.

## Development and release

- [Mission Control browser tests](../tests/mission-control/README.md) gives the local commands for the Chromium suite used in CI.
- [Versioning and release policy](versioning.md) covers SemVer, Git-derived builds and provenance.

## Design and implementation history

- [ADR-0001](adr/0001-versioning.md) records the build and versioning decision.
- [Proposed ADR-0002](adr/0002-provider-routing.md) records the provider-routing design and security trade-offs.
- [Work packages](workpackages/) records historical implementation scope and handovers.
