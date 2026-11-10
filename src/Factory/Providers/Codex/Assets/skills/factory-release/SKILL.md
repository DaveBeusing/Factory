---
name: factory-release
description: Prepare local release manifests, source archives and checksums without deployment.
---

# Release preparation

**Prepare reviewable local artifacts; publication is a separate authorization.**

## Procedure

1. Confirm the project, source revision and explicit SemVer requested by the operator.
2. Invoke `Factory release --project <name> --version <X.Y.Z>`.
3. Inspect the generated local source archive, manifest, checksums and available evidence.
4. Report what is prepared versus what still requires review or release approval.

> [!IMPORTANT]
> Do **not** tag, push, merge, sign, publish or deploy without a separate authorized request. This command does not certify a production release.
