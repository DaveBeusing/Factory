# Factory engineering documentation

This index links to canonical documentation and avoids maintaining competing copies of operational instructions.

- [Root README](../README.md) — installation, build, CLI, supported operations and limitations
- [Mission Control UI](mission-control.md) — connection status, navigation and operator controls
- [Provider routing and budgets](providers.md) — adapters, role routing, usage accounting, capabilities, local token limits and reconciliation
- [Project review and goal creation](project-reviews.md) — deterministic inspection, reviewed findings and goal intake
- [Versioning and release policy](versioning.md) — local SemVer source of truth and Git-derived builds
- [Architecture decision ADR-0001](adr/0001-versioning.md) — build and version governance
- [Proposed architecture decision ADR-0002](adr/0002-provider-routing.md) — provider routing and security tradeoffs
- [Work packages](workpackages/) — historical bounded implementation contracts

The repository source and executable tests establish current behavior. Proposed architecture decisions and work packages do not by themselves constitute deployed enforcement or verified release features.
