# Recover interrupted work and budget reservations

[← Documentation](README.md) · [Configuration](configuration.md) · [Providers](providers.md)

> [!NOTE]
> **Reconcile before retrying.** Missing heartbeat alone does not prove that Git, a remote agent or a provider call did not complete. Preserve worktrees, leases, holds and dispatch/receipt data before re-execution.

Use **Recovery** to inspect interrupted jobs and **Capacity** to reconcile provider turns. A held reservation protects capacity while usage is uncertain. Releasing unused tokens and returning a job to the queue are separate operator actions. See [Provider routing and token budgets](providers.md#budget-semantics) for how the ledgers count consumption and reservations.

## Quarantine expired leases

Run these commands from the Factory repository after building the Windows executable.

```powershell
.\dist\Factory.exe pause
.\dist\Factory.exe status
.\dist\Factory.exe recover
```

Pausing stops new claims. Active jobs continue, and the runner and provider process may still be alive. `recover` quarantines expired active leases as `blocked` at stage `reconcile`, advances the execution generation and holds the unused goal reservation. Late updates from the old lease are rejected. Inspect the durable checkpoint, source commit, worktree and any Git effects before deciding how to proceed.

`recover` also marks active provider reservations as held when their recorded owner is verifiably stopped on the current host. Starting a new planning turn also checks for these owners. Keep the reservations held while final usage is unknown.

### Pause and stop controls

Pausing or stopping a project or goal prevents new scheduling for that scope. A job-level pause or stop requests local cancellation and preserves worktree changes. Review those changes before requeueing an interrupted job. Worker drain and disable affect claim eligibility. Mission Control can stop idle workers launched by the same server instance on the current host. Active remote jobs and externally managed workers require action on their own host.

## Recover provider reservations after a host change

1. Verify that the original runner and its provider process have stopped. Quarantine any expired lease on the restored controller with `recover`. A job linked to the provider turn must be inactive and have no lease owner.
2. In **Capacity → Reservations from another host**, select **Review stopped host** for the active reservation. Confirm the shutdown and enter 10–1000 characters of evidence.
3. Select **Mark reservation held**. The reserved balance stays in place and the job stays in its current state. Use the next section to settle the reservation once final totals are available.

For automation with administrator authentication, submit `POST /api/providers/budget-reservations/{id}/recover` with `reviewed: true` and `reason`. The action applies to active reservations with a recorded owner on a different host.

## Recover a remote executor receipt

Open **Jobs → Inspect → Remote provider turns** to find the turn ID, lease generation, requested and observed model, recorded usage and reason for the hold. If the executor saved the receipt before the connection dropped, Factory can retrieve it without running the provider again.

After quarantining the expired lease and reviewing the interrupted job, select **Recover receipt** on the held turn. Record the review evidence and confirm any verified overrun. Recovery requires the original configured executor, its retained receipt and final usage matching the recorded provider, model and turn. It updates the budget ledgers and leaves the job blocked for separate review.

For automation, send `POST /api/jobs/{id}/provider-turns/{turnId}/recover` with administrator authentication, `reviewed: true` and a `reason` of 10–1000 characters. Include `confirmOverrun: true` after verifying any overrun.

If the executor has no matching receipt, keep the reservation held until you can verify final usage independently. Use the settlement workflow below for that evidence or for turns recorded before schema 11.

### Back up and restore remote execution state

Pause new claims and stop worker, controller and executor processes before taking a coordinated backup. Record the package versions, host roles and backup time. Preserve the controller's SQLite state and settings and the executor's state directory, including `executor-dispatches` and `executor-receipts`. Keep backups access-controlled because settings and receipts can contain credentials or private work output. Keep the executor credential separately in the service secret store.

Dispatch markers in `executor-dispatches` are the executor's protection against repeating a turn. Preserve them even when a receipt is no longer needed. Receipts written by older executors may also be in that directory. Preserve all receipts associated with unresolved holds. A missing or inaccessible receipt after a provider call leaves usage uncertain, and the original dispatch must remain fenced.

Restore into isolated state directories with workers stopped and claim admission paused. Use coordinated copies so the restored executor does not forget a dispatch recorded by the controller. If the backup may predate a provider call, keep the affected executor endpoint isolated and reconcile the uncertain turn before resuming claims. Quarantine expired leases and inspect Git side effects. Verify a retained receipt lookup and settle a held turn before resuming normal operation.

For credential rotation, pause claims and drain or stop remote work. Replace `FACTORY_REMOTE_EXECUTOR_TOKEN` on both the controller and executor through their service secret stores. Restart both services, verify that the new credential can retrieve a retained receipt and that the previous credential is rejected, then resume. Rotation does not require changing grant digests, receipts or dispatch markers.

#### LT-03 restore rehearsal

The local rehearsal on Windows started from `master` at `a21ec18`. It ran separate controller and executor processes with distinct `FACTORY_HOME` directories and a loopback provider fixture. The executor saved a real receipt while the controller retained the interrupted turn without recording the response. Both processes were stopped before copying their state to separate backups and restarting from fresh restore directories.

| Check | Observed result |
| --- | --- |
| Credential rotation | The old token returned HTTP 401 on both executor routes. The rotated credential retrieved the retained receipt, and the restored controller used it for reconciliation. |
| Replay fencing | Reposting the original grant after restart was rejected. Removing the receipt from the restored copy still left the dispatch fenced. The fixture counted one provider invocation. |
| Hold and recovery | The expired lease was quarantined with both budget holds intact. A missing receipt left both holds and their balances unchanged. After restoring the receipt, two recovery requests recorded a single debit of 30 input and 10 output tokens. The provider reservation was released. The goal reservation stayed held and the job stayed blocked for separate review. |

The rehearsal is part of the .NET suite. To run it alone after building the solution in Release mode, use `dotnet tests/Factory.Tests/bin/Release/net10.0/Factory.Tests.dll --verify-remote-restore`. It exercises real Factory processes over loopback with a simulated provider. The live pilot is planned in [package 05](roadmap.md#package-05--live-pilot-for-mixed-provider-routing). The authenticated Claude Code test remains open in [#22](https://github.com/DaveBeusing/Factory/issues/22).

### Downgrade an executor with held turns

The previous executor reads receipt files only from `executor-dispatches`. A controller database rollback does not make files in `executor-receipts` visible to that executor.

1. Pause new claims and stop workers. List the held remote turns in Mission Control and record each turn ID and its lease generation. If possible, recover and settle the held provider usage with the current executor before downgrading. Keep any turn without verified final usage held.
2. Stop the controller and executor and take a coordinated backup of the current controller database, settings and both executor directories. Keep that copy and the current binary for recovery. Preserve the dispatch markers, which are the files named by turn ID without a suffix.
3. If a held turn must remain recoverable on the previous executor, prepare an isolated copy of the executor state. For each affected turn, copy `<turn-id-without-hyphens>.receipt.json` from `executor-receipts` into `executor-dispatches` with the same name. Keep the original and verify that the copy has the same SHA-256. If a destination already exists, compare hashes and stop on a mismatch. Leave the extensionless dispatch marker in place. A missing marker or missing receipt requires investigation before resuming.
4. Start the previous executor against the prepared state and start the controller with claims still paused. Confirm that the controller still contains the matching held turn and grant. Verify the receipt lookup with the configured executor credential. If a compatible controller backup would lose the hold or its grant, use the current controller and executor state to reconcile it first. Resume claims only after the remaining holds and any Git side effects are accounted for.

## Settle held usage

1. In **Capacity → Held provider reservations**, select **Reconcile**. Enter verified final input and output totals for that **provider turn**, acknowledge the independent verification and record the evidence. Totals must be at least as high as the amounts already reported. A linked job must be inactive and have no lease owner.
2. When a turn belongs to a goal-funded job, Factory applies the newly verified token delta to the matching job and goal reservation in the same transaction. The goal reservation stays held until separately settled. If the controller reports missing or inconsistent ledger evidence, inspect the records before continuing.
3. When all linked turns are settled, open **Goal planner → Goal & budget → Review held usage**. Check the cumulative job totals and record the evidence before confirming. For a job with linked provider turns, enter any additional usage on the relevant turn first. A held goal reservation without tracked provider turns can be settled with independently verified cumulative job totals.

Confirm a verified overrun explicitly whenever the verified usage exceeds the applicable reservation. The excess counts against the budget and the confirmation appears in the audit trail. Leave the reservation held when the evidence is incomplete. An older goal reservation cannot be adjusted after a newer reservation exists for the same job.

These endpoints require the administrator token.

| Endpoint | Request fields |
| --- | --- |
| `POST /api/providers/budget-reservations/{id}/reconcile` | `finalInputTokens`, `finalOutputTokens`, `reviewed: true`, `reason` and `confirmOverrun: true` when applicable |
| `POST /api/goals/{goalId}/budget/reconcile` | `reservationId`, `finalTokensIn`, `finalTokensOut`, `reviewed: true`, `reason` and `confirmOverrun: true` when applicable |

## Return a reviewed local job to the queue

Use this path for a quarantined local job with a verifiable previous runner on the current host. Factory checks that the previous execution has stopped, the durable checkpoint is `worktree` or `agent`, and the accessible worktree is clean at the recorded commit. Remote jobs, jobs from another host and jobs with uncertain commit, push or PR effects require manual investigation.

```powershell
.\dist\Factory.exe reconcile '<job-guid>' --reviewed --reason 'Reviewed the previous process and the clean worktree'
```

The equivalent administrator action is `POST /api/jobs/{id}/reconcile` with `reviewed: true` and `reason`. A successful reconciliation records the decision and returns the job to `queued`. An unresolved goal hold still prevents a new claim. After reconciling the job and its budgets, use `resume` to allow new claims if the queue is paused. A subsequent runner claim uses a new lease and generation. Lease fencing protects Factory state. Inspect GitHub and the worktree separately for any effects that already occurred.

## Restore or upgrade state

Stop Factory before backing up its state and preserve the existing worktrees. To downgrade, restore a database backup compatible with the previous binary. The schema 3 migration quarantines active goal jobs that had no recorded reservation. The schema 7 migration moves active provider reservations with no recorded process owner to held. Review those records through the same evidence-based settlement workflow.
