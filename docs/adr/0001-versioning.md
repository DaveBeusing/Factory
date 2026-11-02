# ADR-0001: Git-derived preview versions with a central MSBuild release baseline

- **Status:** Accepted for implementation
- **Date:** 2026-11-02
- **Scope:** Factory product builds and release preparation
- **Decision owner:** Repository maintainer

## Context

Factory develops through short-lived PR branches, sometimes concurrently across multiple agents. Incrementing `Directory.Build.props` in every implementation branch would create version conflicts and cause serial file edits to dominate integrations. Build versions must be determinable without GitHub, tags, npm or a hosted version service.

## Decision

Use SemVer 2.0 with `preview`, `rc` and `stable` channels. Store the product `VersionPrefix`, selected channel and explicit fallback/RC sequence in root `Directory.Build.props`, consumed by every .NET project. For full Git checkouts, derive the preview sequence from the local first-parent commit count of HEAD at build time. Embed the full source commit in a manifest and a short commit ID in the assembly informational version. CI must check the provenance and run existing tests. Product-level bumps and channel transitions are explicit changes reviewed through pull requests.

Keep product SemVer separate from SQLite schema and eventual worker/provider contract versions. No automatic publishing or auto-merge is implied.

## Alternatives

1. **Patch bump on every commit:** rejected because commits are not releases and parallel work generates conflicts.
2. **Post-merge bot committing version bumps to master:** rejected because it violates the strict PR rule and creates duplicate/lagging version PRs.
3. **Git tags as the only version authority:** rejected because local tag availability and hosting conventions would become essential to builds.
4. **External GitHub run numbers:** rejected because they do not work for local/offline builds.

## Consequences

Preview numbers can skip values and be equal on unrelated branches; commit metadata disambiguates binaries. Shallow clones must be expanded before creating official preview artifacts. Direct `dotnet build` uses a deterministic fallback sequence; `Build.ps1` is the provenance-aware packaging route. Release signing, distribution and supported-version compatibility policy remain separate gated work.
