---
name: factory-plan
description: Inspect master and existing implementations before planning development work packages with Factory .NET.
---
# Master-first planning

> [!IMPORTANT]
> Adoption is a separate human decision. A completed preflight cannot silently grant permissions to implement, push, or merge.

## Procedure

Every work package starts from **current master**. Do not use another PR's branch as the implementation base or assume that absence on master means the work does not exist in an open PR.

1. Register the project with `--base master` and ensure the project checkout is a **clean master checkout** before generating a plan.
2. Create a goal using `Factory goal add --project ... --objective ... --criteria ...`.
3. Run `Factory plan --goal <guid>`; Factory records the exact master commit, source candidates, existing Factory jobs and any accessible open GitHub PR candidates. A missing or unauthenticated GitHub CLI is reported as unverified, **not** as zero PRs.
4. Inspect what already exists. If a feature is complete, do not recreate it. If incomplete or defective, first propose the smallest package to finish, repair and validate the existing implementation. Review and integrate overlapping PRs through normal authorization rather than generating replacements.
5. Review dependencies, expected paths, risk, tests and acceptance evidence for each package. Work packages may run in parallel only when ownership and integration prerequisites are safe.
6. Use `Factory adopt --goal <guid> --plan-revision <reviewed-revision-guid> --mode local|remote --reviewed-existing-work` **only after** a human has reviewed the preflight candidates and explicitly approved adoption. Omit the acknowledgement flag if review is not complete. A stale master or changed open PR inventory requires a refreshed plan.
7. Direct `Factory enqueue` work packages also require an existing-code and open-work check, plus `--reviewed-existing-work` after user review.
8. Continue with strict PR review against master. Do not auto-merge, force-push or discard partial worktrees.

The inspection is heuristic and does not certify feature completeness. The operator must confirm current source and pending PR state. If the project origin is not a supported GitHub repository, review outstanding external work manually.
