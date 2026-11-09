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

A second invocation also persisted all 384 claims exactly once. The drain times ranged from 2.07 to 4.73 seconds with one caller and from 1.11 to 1.52 seconds with eight. The contended SQLite acquisitions took 301.49–301.82 ms.

These are local comparison values for this fixed workload. They leave production capacity and alert thresholds open. Package 05 remains skipped and the authenticated Claude Code check stays tracked in [#22](https://github.com/DaveBeusing/Factory/issues/22).
