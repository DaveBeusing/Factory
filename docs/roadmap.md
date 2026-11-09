# Factory long-term roadmap

## Baseline

Verified on 2026-11-09 against `master` at `037f2a1`. The [master CI run](https://github.com/DaveBeusing/Factory/actions/runs/42707820123) passed the .NET suite, Chromium, provenance checks and Windows package installation, upgrade and backup restore. GitHub listed no open PRs, releases or tags. The only open issue was [#22](https://github.com/DaveBeusing/Factory/issues/22).

Master requires `build-and-test` from GitHub Actions app `15368` on an up-to-date branch and blocks force-pushes. The separate review rule requires one approval and has a PR-only bypass for the repository owner. The required check has no bypass actor. See [integration policy](planning-preflight.md#lt-02-test-results).

Project review, goal planning, master-first adoption, provider attribution, local limits, remote lease verification and Mission Control recovery are available. The current Windows x64 preview is framework-dependent. Codex account windows apply to the controller account. Remote Codex and Ollama report token usage but no USD amount. The earlier live remote pilot, package 05, stays skipped. Issue #22 retains ownership of the authenticated Claude Code cost verification and depends on account access.

## Execution order

Horizons describe milestones. The implementation layers are sequential because the work shares CI, state models, dashboard surfaces and regression fixtures. Before starting a package, recheck current `master`, CI and open PRs and issues, then extend matching work and confirm the expected paths before implementation.

| Horizon | Order | Completion milestone |
| --- | --- | --- |
| Verified preview | LT-01 → LT-02 | A Windows package from a known commit can be verified and restored. The integration policy has been exercised. |
| Operable remote installation | LT-03 → LT-04 | Recovery is repeatable under interruption and credential changes. Project access is enforced for human operators. |
| Evidence for broader support | LT-05 → LT-06 → LT-07 | Adapter claims, upgrade compatibility and operating limits have recorded evidence. |
| Release candidate and stable decision | LT-08 | The supported feature and platform matrix has explicit sign-off. |

## Status on master

| Package | Status | Merged evidence and remaining work |
| --- | --- | --- |
| LT-01 | Preview baseline delivered | #33 added the Windows preview rehearsal and pinned the Node action. Current CI exercises the package on Windows x64. The full release platform matrix belongs to LT-07. [Versioning](versioning.md#windows-preview-package-validation) |
| LT-02 | Active | #34–#35 verified required-check identity, exercised the integration policy and documented the active rulesets. [Integration policy](planning-preflight.md#lt-02-test-results) |
| LT-03 | Recovery baseline delivered | #36 and #38 separated receipt storage from dispatch fencing and exercised coordinated controller and executor restore, replay protection and credential rotation. Evidence comes from a controlled rehearsal. [Recovery](recovery.md#lt-03-restore-rehearsal) |
| LT-04 | Preview acceptance recorded | #39–#53 delivered project-scoped operator controls and Mission Control. #55 recorded the manual playtest and exercised administrator migration, access recovery and token rotation. [Operator authorization](operator-authorization.md) |
| LT-05 | Contract baseline delivered | #56–#57 verified accounting semantics, stale allowances and two historical/current client-executor combinations. Historical receipt lookup has a documented compatibility boundary. The authenticated Claude Code check remains in [#22](https://github.com/DaveBeusing/Factory/issues/22). [Provider contract](providers.md#provider-evidence-contract) |
| LT-06 | Local baseline delivered | #58–#60 measured queue drain, claims, SQLite contention, holds, receipt recovery and disk growth. Warning levels apply to comparable synthetic runs. Production load limits, alert levels and operational instrumentation remain open. [Measurements](queue-load-baseline.md) |
| LT-07 | In progress | #61 documented the Windows, .NET and SQLite matrix and its gaps. Runtime-only installation, real historical databases to schema 18, rollback and component compatibility still need evidence. [Platform matrix](versioning.md#lt-07-platform-and-upgrade-matrix) |
| LT-08 | Pending | Prepare and validate an exact-commit release candidate after the selected LT-07 rows have passed. Sign-off and publication remain separate decisions. |

## LT-01 · Repeatable Windows preview

- **Objective.** Produce one reviewable Windows x64 preview from the exact `master` commit. Preserve the current framework-dependent packaging contract.
- **Scope.** `Build.ps1`, `.github/workflows/dotnet-validation.yml`, `tests/Factory.Tests/WindowsPackageValidation.cs` and `docs/versioning.md`.
- **Acceptance.** Build manifest and checksums match the packaged files and commit. A clean Windows account can install and run the extracted package. An upgrade from the current deployed schema, the existing historical schema 8 fixture and restoration from a preserved backup pass. Release notes list the runtime requirement, supported upgrade path and pending live tests. Trace the observed Node warning to its source and verify the result of the fix in CI or record the upstream limitation.
- **Validation.** Clean-install and upgrade smoke tests, manifest verification, SHA-256 checks and a restore drill. GitHub publication is a separate decision.

## LT-02 · GitHub integration policy

- **Depends on LT-01.** Agree on required checks and review rules for `master`, including the treatment of overrides. Verify a policy against a disposable or explicitly authorized repository before applying it to `master`.
- **Scope.** GitHub branch settings, `src/Factory/RemoteResultVerification.cs`, `.github/workflows/dotnet-validation.yml`, `tests/Factory.Tests/RemoteProviderLeaseTests.cs` and `docs/planning-preflight.md`.
- **Acceptance.** Normal merges require the intended check and review policy. Factory rejects a missing check, a wrong-source check when a check is pinned to an app, and results for a different head. Record which GitHub rules and check sources the verifier supports. Unsupported required workflow rules stay blocked with an actionable reason until a trustworthy verifier exists for them.
- **Validation.** Exercise a draft PR with a missing result, an untrusted same-name result, a force-push and a passing head. Verify the policy independently of the agent's local review. Applying repository settings requires operator approval.

## LT-03 · Recovery and maintenance for remote execution

- **Depends on LT-02.** Establish a controller and executor recovery procedure with explicit storage and credential ownership.
- **Scope.** `src/Factory/RemoteProviderExecutor.cs`, `src/Factory/RemoteProviderRouting.cs`, `src/Factory/JobRecovery.cs`, `src/Factory/StateStore.cs`, `tests/Factory.Tests/RemoteProviderLeaseTests.cs` and `docs/recovery.md`.
- **Acceptance.** Document a consistent backup of controller state and executor receipts. Separate receipt retention from duplicate-dispatch protection. A receipt involved in an unresolved hold remains recoverable, and cleanup never makes a spent grant runnable again. Document executor credential rotation, expired lease handling and failure modes when receipt storage is unavailable.
- **Validation.** Fault-injection tests cover executor restart, response loss, stale lease and disk failure. Restore rehearsals demonstrate one debit per turn, recoverable holds and no duplicate provider dispatch. This uses fixtures while the real pilot remains skipped.

## LT-04 · Human operator authorization

- **Depends on LT-03.** Define project-scoped human permissions for viewing evidence, planning, execution control and financial reconciliation. Preserve the worker authorization boundary.
- **Scope.** `src/Factory/Dashboard.cs`, `src/Factory/WorkerAuthentication.cs`, `src/Factory/StateStore.cs`, Mission Control and API tests, and `docs/mission-control.md`.
- **Acceptance.** An operator cannot read or mutate another project's jobs, ledgers or receipts through direct API calls or the UI. Approval, recovery and budget decisions record a human identity and reason. Existing single-operator installations have a documented migration path. Role revocation and recovery of administrator access have explicit behavior.
- **Validation.** Run a deny-by-default API matrix for projects, roles, streams and recovery, plus Chromium tests for authorized and denied actions.

## LT-05 · Provider evidence and accounting contracts

- **Depends on LT-04.** Define a versioned capability matrix for requested and observed model, final usage, reported cost and allowance source. Keep account windows separate from local token ceilings.
- **Scope.** `src/Factory/ProviderRouting.cs`, `src/Factory/Providers/Codex/UsageMonitor.cs`, `src/Factory/RemoteProviderRouting.cs`, `src/Factory/RemoteProviderExecutor.cs`, adapter tests and `docs/providers.md`.
- **Acceptance.** Mixed controller and executor versions have a documented compatibility result. An older receipt with no amount remains readable. Reported amounts, including zero, reach the correct project and provider totals once. Unavailable values stay unknown. Show an executor account allowance only if the adapter can identify the account scope and provide a fresh report.
- **Validation.** Fixture tests cover versions, replay, reported zero, unknown cost and stale account data. Keep the real Claude Code check in [#22](https://github.com/DaveBeusing/Factory/issues/22). Remote Claude execution and separate executor-account quotas require separate evidence before adding them to the support matrix.

## LT-06 · Observability and capacity limits

- **Depends on LT-05.** Measure queue and recovery behavior before changing scheduler or storage architecture.
- **Scope.** `src/Factory/Scheduler.cs`, `src/Factory/StateStore.cs`, `src/Factory/Dashboard.cs`, Mission Control and a synthetic load fixture.
- **Acceptance.** Record a repeatable baseline for queue wait, claim latency, SQLite contention, provider holds, receipt recovery and disk growth. Correlate records by project, job, lease generation and turn. Define an operating envelope and thresholds from the measured baseline. Optimize snapshot reads or write paths where measurements show a bottleneck.
- **Validation.** Run the same load fixture before and after changes. Verify that holds, replay fencing and project isolation remain correct under concurrency. Keep credentials and provider prompts out of operational telemetry.

## LT-07 · Supported upgrade and platform matrix

- **Depends on LT-06.** Define which Windows versions, .NET runtimes, controller and worker versions, SQLite schemas and plugin contracts are supported. Evaluate additional platforms against a demonstrated operating need.
- **Scope.** `Build.ps1`, `.github/workflows/dotnet-validation.yml`, `tools/Factory.Versioning/`, `tests/Factory.Tests/WindowsPackageValidation.cs` and `docs/versioning.md`.
- **Acceptance.** For each supported combination, record a repeatable install and upgrade path, a rollback via a compatible backup, and which component upgrades must be coordinated. Preserve documented public CLR contracts or announce breaking changes explicitly. Add another platform to the published matrix only when its install, worker and restore tests are in CI.
- **Validation.** Test current and previous supported packages, old workers and saved receipts, and an independently compiled plugin or public-API consumer.

## LT-08 · Release candidate and stable decision

- **Depends on LT-07.** Prepare a release candidate from an exact commit and document the support scope, release evidence and rollback procedure.
- **Scope.** `Directory.Build.props`, release provenance and artifact verification, CI packaging and `docs/versioning.md`.
- **Acceptance.** The release review covers compatibility, security, licensing, artifact integrity, support ownership and recovery. Decide artifact signing and distribution. Every capability advertised for stable has corresponding acceptance evidence. If the mixed remote pilot remains skipped or #22 remains open, label the affected capabilities with the status supported by the available evidence.
- **Validation.** Run the release-scope matrix on the candidate artifacts and repeat the restore drill. The RC transition, stable transition and publication are separate approval decisions.

## Proposed roadmap extensions

| Priority | Proposed work | Acceptance evidence |
| --- | --- | --- |
| Next, LT-07A | Upgrade and rollback using genuine historical packages. Start with the schema 8 and 10 builds already used in prior rehearsals. Select the previous candidate that will be advertised as supported. | Seed state with the older executable, upgrade it to schema 18 and upgrade a separate backup copy. Check database integrity, state preservation and backup hashes. Restore the compatible backup and start the older executable against it. Add a test for refusal of newer schemas before any database write. |
| Next, LT-07B | Verify the x64 package on selected Windows client and server versions with only the .NET and ASP.NET Core shared frameworks installed. | Record exact Windows, runtime and native SQLite versions. Exercise the minimum selected runtime patch, the current patch, a missing framework and an incompatible architecture. Verify the extracted package hash and installation path. |
| Before LT-08, LT-07C | Record controller, worker and executor upgrade order and the public API compatibility boundary. | Run matching and selected mixed versions against retained receipts and open holds. Test an independently compiled consumer of the public API and publish the tested version pairs with their limitations. |
| Before publication, LT-08A | Prepare an evidence bundle for the candidate. | Tie checksums, compatibility results, restore evidence, dependency and licensing review, and signing and distribution decisions to one commit and package. Record which features carry preview limitations. |
| Proposed LT-09 | Measure production-like capacity and operational alerts. | Run sustained mixed workloads on representative storage. Record queue age, recovery backlog, SQLite retries, peak WAL and retention growth. Set alert windows only after agreeing on service objectives and validating them against healthy and failing runs. |
| Proposed LT-10 | Establish dependency and release maintenance. | Define a supported-version and end-of-support policy, an update cadence for .NET, SQLite and provider CLIs, and ownership for security fixes and reproducible patch releases. |

Start with LT-07A because the current package rehearsal uses synthetic input and the rollback path determines which prior versions can be supported. LT-07B can proceed once the target Windows images and runtime versions are selected. Keep package 05 skipped. Revisit the authenticated Claude Code test through [#22](https://github.com/DaveBeusing/Factory/issues/22) when account access is available.
