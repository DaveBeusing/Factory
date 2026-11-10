---
name: factory-knowledge
description: Index source relationships and maintain traceable project knowledge in Factory.
---

# Project knowledge

**Build a heuristic source graph before interpreting impact.**

## Procedure

1. Run `Factory graph --project <name>` to refresh the graph against the intended project source.
2. Run `Factory impact --project <name> --file <relative-path>` for a bounded impact view.
3. Store documented decisions with `Factory knowledge --project <name> --topic <topic> --content <content>`, including source references when available.

> [!NOTE]
> Import and symbol matching are **heuristic**, not complete semantic dependency analysis. Label unknown relationships instead of reporting full coverage.
