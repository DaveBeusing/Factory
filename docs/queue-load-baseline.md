# LT-06 queue and SQLite load baseline

Measured on 2026-11-09 from `master` at `d02be93` with the measurement fixture applied. The host reported Windows build 26300, x64, 24 logical processors, .NET runtime 10.0.14 and SQLite 3.54.0. The [master CI](https://github.com/DaveBeusing/Factory/actions/runs/42688852253) was green. There were no open pull requests and [#22](https://github.com/DaveBeusing/Factory/issues/22) was the only open issue.

## Reproduce

Run from the repository root with the .NET 10 SDK:

```powershell
dotnet run --project tests/Factory.Tests/Factory.Tests.csproj -c Release -- --measure-queue-load
```

The fixture emits JSON with environment information and per-round results. It uses fresh temporary databases, warms up with eight jobs, then runs three rounds per configuration in alternating order. Each round starts with 64 eligible remote jobs in one project. Job paths are disjoint and claim capacity is 64. One or eight callers use the production `WorkerProtocol.Claim` path and share a start signal after the queue has been seeded. The fixture checks persisted leases and events for one claim per job. Jobs stay active through the end of the round. The workload covers claims within one controller process. It omits HTTP transport, job execution, provider calls and goal reservations.

- **Claim latency** measures entry to return from `WorkerProtocol.Claim`, including recovery checks, scheduling and SQLite persistence.
- **Queue-to-claim** measures the common release signal to the return of each committed claim. Its maximum is the batch drain time. Throughput divides 64 claims by that time.
- **SQLite acquisition** measures `BeginTransaction(deferred: false)` in a separate WAL database. After 20 idle acquisitions, a second connection attempts to acquire the write transaction while the first holds it for about 250 ms. Both connections use a 30-second timeout. This measures writer acquisition under controlled contention.

All durations are milliseconds. Percentiles use nearest rank within each round.

| Round | Callers | Claim p50 | Claim p95 | Claim max | Queue-to-claim p50 | Queue-to-claim p95 | Drain | Claims/s |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 1 | 56.44 | 142.05 | 298.97 | 2847.18 | 4661.16 | 4776.31 | 13.40 |
| 1 | 8 | 44.92 | 462.19 | 1205.79 | 608.13 | 1378.17 | 1582.80 | 40.43 |
| 2 | 8 | 36.21 | 383.95 | 870.79 | 508.85 | 1013.18 | 1134.95 | 56.39 |
| 2 | 1 | 33.50 | 41.57 | 45.01 | 1068.35 | 2123.19 | 2233.13 | 28.66 |
| 3 | 1 | 34.98 | 43.67 | 266.36 | 1257.18 | 2428.03 | 2530.41 | 25.29 |
| 3 | 8 | 19.87 | 586.53 | 1190.99 | 564.96 | 1289.42 | 1366.47 | 46.84 |

All 384 measured claims persisted once. Eight callers drained the batch faster in each round and had higher p95 claim latency. The idle SQLite writer acquired the transaction in 0.01 ms at p50 and 0.02 ms at p95. The held writer released after 250.11–251.85 ms, measured from the second caller's attempt. The second writer acquired after 300.94–301.07 ms. All three attempts completed within the timeout.

A second invocation also persisted all 384 claims exactly once. The drain times ranged from 2.07 to 4.73 seconds with one caller and from 1.11 to 1.52 seconds with eight. The highest per-round claim p95 was 140.19 ms with one caller and 604.66 ms with eight. The contended SQLite acquisitions took 301.49–301.82 ms.

These measurements support the provisional comparison limits below. Package 05 remains skipped and the authenticated Claude Code check stays tracked in [#22](https://github.com/DaveBeusing/Factory/issues/22).

## Provider holds, receipt recovery and storage

The same command also reports `recovery`. This measurement starts from `master` at `0ac267a` with the extended fixture applied and ran on the Windows and .NET configuration above. It adds three batches of 16 expired remote jobs to one controller database. Each job has an active provider turn, a goal reservation and a synthetic receipt in a separate executor directory. Prior batches remain in the database.

The quarantine timer covers one production `JobRecovery.QuarantineExpired` call. Four callers then read the saved receipts through `RemoteProviderExecutor.ReadReceiptAsync` and settle them through `RemoteProviderRouting.SettleRecoveredReceipt`. Recovery latency covers the file read and settlement. The batch timer includes dispatching the four callers. Fixture setup, assertions and replay checks are outside the timed sections. The workload uses synthetic evidence and local files. HTTP transport, executor dispatch and real provider calls are outside its scope.

Durations are in milliseconds. SQLite sizes are the controller database file length after quarantine and after recovery. Executor receipt files are reported separately.

| Cumulative turns | Quarantine per batch | Recovery per batch | Receipt p50 | Receipt p95 | SQLite held, KiB | SQLite recovered, KiB | Receipt bytes |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 16 | 31.92 | 206.27 | 21.02 | 181.27 | 468 | 472 | 9,888 |
| 32 | 30.76 | 203.99 | 22.33 | 181.25 | 536 | 548 | 19,776 |
| 48 | 34.91 | 264.30 | 32.52 | 180.63 | 608 | 620 | 29,664 |

The seeded database started at 436 KiB and grew by 184 KiB after 48 recoveries. SQLite WAL and SHM files were absent at the sampled boundaries after connections closed. Peak WAL size during writes remains unmeasured. With 16 receipts per batch, nearest-rank p95 equals the slowest receipt in that batch.

All 48 expired turns entered quarantine and settled exactly once. Replaying the receipts with a changed cost preserved the original amounts and token counts. Settlement released provider reservations, kept goal reservations held and left jobs blocked at the fenced generation.

## Provisional operating limits and warnings

Reviewed against `master` at `2adcb8e`. The measured queue envelope is one controller process, one project and bursts of 64 eligible remote jobs with disjoint paths, at one and eight claim callers. The measured recovery envelope is three batches of 16 expired jobs, with four concurrent receipt recoveries per batch and 48 turns retained in the database. Queue and recovery ran separately. Use these configurations for comparisons. Larger queues, overlapping workloads and additional concurrency require their own measurements.

For comparable runs, the warning levels add roughly 25% headroom to the highest observed sample and round up. Flag a breach and repeat the same configuration on a comparable host. A repeated breach warrants investigation. These are manual regression signals for the fixture. Production alert levels remain open.

| Signal | Highest observed | Warn above |
| --- | ---: | ---: |
| Drain 64 jobs, one caller | 4.78 s | 6 s |
| Drain 64 jobs, eight callers | 1.58 s | 2 s |
| Claim p95, one caller | 142.05 ms | 180 ms |
| Claim p95, eight callers | 604.66 ms | 800 ms |
| SQLite write acquisition with a 250 ms hold target | 301.82 ms | 400 ms |
| Quarantine 16 expired jobs | 34.91 ms | 45 ms |
| Recover 16 receipts with four callers | 264.30 ms | 350 ms |
| Slowest receipt in a 16-receipt batch | 181.27 ms | 230 ms |
| Controller SQLite file growth after 48 recoveries | 184 KiB | 240 KiB |
| Retained synthetic receipt files after 48 recoveries | 29,664 bytes | 37 KiB |

Treat any SQLite write timeout, duplicate or missing claim, lost hold, duplicate debit or broken lease fence as a failed run on first occurrence. Hold age and backlog need separate thresholds because the fixture creates expired jobs in batches and immediately recovers every available receipt. Storage growth thresholds compare the same starting database and retained workload.

### Evidence needed for production values

- **Queue and claims.** Measure sustained arrival rates, bursts and queue age across projects, including dependencies, overlapping paths, worker polling and HTTP. Sweep caller counts around saturation. Collect enough samples to estimate tail latency and correlate it with throughput, worker count and SQLite retries.
- **Holds and recovery.** Run recovery while claims and other writes continue. Include unavailable executors, missing and delayed receipts, retries, and realistic receipt payloads. Measure hold age, backlog, recovery success and drain time. Use the results to set a recovery objective and an escalation point for accumulating holds.
- **SQLite and disk.** Run longer tests with representative retained history on target storage. Sample the peak WAL during writes, checkpoint behavior with long-lived readers, free disk, and growth under the configured retention policy. Repeat with backup and restore activity and measure bytes for controller data and executor receipts separately.
- **Alert calibration.** Repeat across target deployment sizes and resource profiles. Record latency distributions, error rates and saturation alongside CPU, memory and disk I/O. Set thresholds and evaluation windows against the agreed service objectives, then validate alert behavior with representative healthy load and controlled failures.
