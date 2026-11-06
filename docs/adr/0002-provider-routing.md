# ADR-0002: Role-scoped AI provider routing and accountable token reservations

- **Status:** Proposed for maintainer review
- **Date:** 2026-11-06
- **Scope:** Runtime adapters, project routing, provider usage, quotas, budgets, security and persistence

## Context

Factory's reference agent is Codex, but the product charter requires a provider-independent core and mixed providers within one project. Existing goal reservations protect concurrent execution but were not attributable by provider or model. Provider account allowances are not derivable from observed local token use.

## Decision proposed

1. Maintain an explicit adapter registry, first shipping Codex's existing exec/app-server modes and a local Ollama text-only adapter. Other integrations require their own capability and security reviews.
2. Route by project and operation role: analysis, planner, builder, general review and optional individual reviewer roles. The default remains Codex; individual reviewers can inherit a review route.
3. Fail closed if a provider is disabled, unknown or cannot supply required capabilities. In particular, local text-only Ollama must not write source files.
4. Persist provider-scoped usage reports in relational SQLite with project/goal/job scope, invocation provenance, model and role. Historical unattributed usage must remain unattributed.
5. Make total provider-profile and project/provider token caps optional, with a separate per-invocation liability reservation. Reserve atomically, enforce caps, release only against reported final usage, and hold ambiguous consumption until operator reconciliation.
6. Preserve the independent goal-budget ledger. Do not turn local caps into claims about provider account entitlement, rolling weekly allowance or billing.
7. Keep provider credentials out of Git and HTTP responses. Restrict the initially supported local Ollama HTTP endpoint to loopback; no implicit remote transport or unsecured network capability.
8. Keep strict PR governance and commit-bound checks. Nothing in provider routing authorizes push, merge, deployment, sandbox downgrade or automatic model fallback.

## Alternatives and trade-offs

- **One global provider:** rejected because it cannot serve mixed roles and workloads.
- **Automatic fallback to any configured provider:** rejected until capability equivalence, prompt disclosure, budget accounting and policy inheritance can be proved safe.
- **Estimate account allowance from local tokens:** rejected because shared accounts and proprietary allowance policies make such estimates untrustworthy.
- **Start with arbitrary remote API keys:** deferred pending dedicated credential storage, audit, permission and model/tool contracts.
- **Charge only completed jobs:** rejected because concurrent, failed and interrupted work may consume tokens. Uncertain final usage must preserve an outstanding liability.
- **In-place restructuring of all existing goals/jobs:** rejected in favor of additive relational tables and existing execution interfaces.

## Migration and compatibility

Schema 7 adds provider assignments, project limits, usage entries and reservation rows without reinterpreting historical job usage. Existing schema 2–6 upgrade paths are retained. State belongs in `.factory`, not the source tree. An operator should back up their Factory state before production upgrades.

## Verification required before acceptance

- Codex planning/building/review continues with no provider overrides.
- Ollama can run only read-only text tasks and reports token counts when provided.
- Cross-project routing does not leak assignments.
- Simultaneous reservations cannot oversubscribe the same configured cap.
- Missing final usage retains the relevant held liability and requires reconciliation.
- Goal budget accounting still blocks overspending.
- CI includes persistence/migration, negative routing, and UI contract tests.
- Manual Windows browser validation is documented separately.

## Open limitations

This is a foundation, not a complete provider ecosystem. Provider API credits and billing remain unknown; remote-worker routing still needs end-to-end proofs. Project review currently relies on deterministic observations rather than provider-driven semantic code audit. Rollback of the schema requires restoring a prior backed-up Factory state, not silently truncating new usage.
