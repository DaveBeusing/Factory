---
name: factory-eval
description: Run a deterministic or project-scoped agent evaluation suite in the .NET Factory.
---

# Evaluation

**Evaluate a defined suite, then report observed outcomes without extrapolation.**

## Procedure

1. Inspect the suite definition and confirm which cases are deterministic versus provider-backed.
2. For deterministic checks, invoke `Factory eval --suite <suite-file>`.
3. For `agent_answer_contains` cases that require a project, add `--project <name>` and use a permitted read-only agent context.
4. Record the number of cases, actual passing/failing outcomes and any missing provider prerequisites.

> [!CAUTION]
> A small passing suite does **not** establish broad agent accuracy or production reliability. Never replace missing evidence with a confidence claim.
