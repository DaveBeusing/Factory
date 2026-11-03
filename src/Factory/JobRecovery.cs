using System.Diagnostics;
using System.Security.Cryptography;

namespace Factory;

public readonly record struct ExecutionFence(long Generation, string Token, string Owner);

public sealed class LostExecutionLeaseException(string message) : InvalidOperationException(message);

/// <summary>
/// Durable claim/fence policy shared by controller, local runner and remote protocol.
/// A lease expiry quarantines work; it never grants permission to replay it.
/// </summary>
public static class JobRecovery
{
    public static ExecutionFence Assign(FactoryJob job, string owner, int leaseSeconds, bool local)
    {
        if (job.State != JobStates.Queued || !string.IsNullOrEmpty(job.LeaseOwner))
            throw new InvalidOperationException("Job is not claimable");
        job.ExecutionGeneration = checked(job.ExecutionGeneration + 1);
        job.LeaseToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        job.LeaseOwner = owner;
        job.LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(leaseSeconds);
        job.Attempt++;
        job.State = JobStates.Preparing;
        job.Stage = local ? "worktree" : "remote_claimed";
        job.CheckpointStage = job.Stage;
        job.CheckpointAt = DateTimeOffset.UtcNow;
        if (local)
        {
            using var process = Process.GetCurrentProcess();
            job.WorkerProcessId = process.Id;
            job.WorkerProcessStartedAt = new DateTimeOffset(process.StartTime.ToUniversalTime());
        }
        else
        {
            job.WorkerProcessId = 0;
            job.WorkerProcessStartedAt = null;
        }
        job.StartedAt ??= DateTimeOffset.UtcNow;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        return new ExecutionFence(job.ExecutionGeneration, job.LeaseToken, owner);
    }

    public static bool Matches(FactoryJob job, ExecutionFence fence) =>
        job.ExecutionGeneration == fence.Generation &&
        !string.IsNullOrEmpty(fence.Token) &&
        string.Equals(job.LeaseToken, fence.Token, StringComparison.Ordinal) &&
        job.LeaseOwner == fence.Owner &&
        job.LeaseUntil > DateTimeOffset.UtcNow &&
        JobStates.Active.Contains(job.State);

    public static void Require(FactoryJob job, ExecutionFence fence)
    {
        if (!Matches(job, fence))
            throw new LostExecutionLeaseException("Execution lease expired or generation was fenced");
    }

    public static void Checkpoint(FactoryJob job, string stage, string? commit = null)
    {
        job.CheckpointStage = stage;
        if (commit is not null) job.CheckpointCommit = commit;
        job.CheckpointAt = DateTimeOffset.UtcNow;
    }

    public static IReadOnlyList<Guid> QuarantineExpired(StateStore store, string? mode = null)
    {
        return store.Change(s =>
        {
            var ids = new List<Guid>();
            foreach (var job in s.Jobs.Where(j => (mode is null || j.Mode == mode) &&
                !string.IsNullOrEmpty(j.LeaseOwner) &&
                j.LeaseUntil <= DateTimeOffset.UtcNow &&
                JobStates.Active.Contains(j.State)))
            {
                var previousOwner = job.LeaseOwner;
                var previousGeneration = job.ExecutionGeneration;
                job.ExecutionGeneration = checked(previousGeneration + 1); // fence old owner first
                job.State = JobStates.Blocked;
                job.Stage = "reconcile";
                job.Error = "Lease expired; inspect the prior process, checkpoint, worktree and external Git effects before retry";
                job.LeaseOwner = "";
                job.LeaseToken = "";
                job.LeaseUntil = null;
                job.UpdatedAt = DateTimeOffset.UtcNow;
                s.Events.Add(new FactoryEvent { JobId = job.Id, Type = "reconciliation_required",
                    Message = $"Previous owner={previousOwner}; generation={previousGeneration}; checkpoint={job.CheckpointStage}" });
                ids.Add(job.Id);
            }
            return (IReadOnlyList<Guid>)ids;
        });
    }

    private static void EnsurePreviousRunnerStopped(FactoryJob job)
    {
        if (job.Mode != JobExecutionModes.Local || job.WorkerProcessId <= 0 ||
            job.WorkerProcessStartedAt is null)
            throw new InvalidOperationException("Previous runner identity is unknown; manual recovery required");
        try
        {
            using var process = Process.GetProcessById(job.WorkerProcessId);
            var started = new DateTimeOffset(process.StartTime.ToUniversalTime());
            if (started == job.WorkerProcessStartedAt)
            {
                if (process.Id != Environment.ProcessId || LocalJobControl.IsRegistered(job.Id))
                    throw new InvalidOperationException("Previous execution process is still alive");
            }
            // Different start time is a reused PID, not the previous execution.
        }
        catch (ArgumentException)
        {
            // No process with that PID is alive.
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("Cannot establish previous worker termination", ex);
        }
    }

    /// <summary>
    /// Explicit operator reconciliation; never resumes automatically and never
    /// replays commit, push, PR or an uninspected dirty worktree.
    /// </summary>
    public static FactoryJob ReconcileLocal(StateStore store, Guid id, bool reviewed, string reason)
    {
        if (!reviewed || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 10)
            throw new InvalidOperationException("Explicit worktree and process review with a reason is required");

        var snapshot = store.Job(id);
        if (snapshot.State != JobStates.Blocked || snapshot.Stage != "reconcile" ||
            !string.IsNullOrEmpty(snapshot.LeaseOwner))
            throw new InvalidOperationException("Job is not waiting for reconciliation");
        EnsurePreviousRunnerStopped(snapshot);
        if (snapshot.CheckpointStage is not ("worktree" or "agent") ||
            string.IsNullOrWhiteSpace(snapshot.Worktree) || string.IsNullOrWhiteSpace(snapshot.CheckpointCommit))
            throw new InvalidOperationException("Unsafe checkpoint; inspect branch, commit and external effects manually");
        if (GitOperations.Head(snapshot.Worktree) != snapshot.CheckpointCommit)
            throw new InvalidOperationException("Worktree HEAD differs from last durable checkpoint");
        var status = Commands.Run("git",
            ["-C", snapshot.Worktree, "status", "--porcelain", "--untracked-files=all"],
            Environment.CurrentDirectory);
        if (!status.Ok || !string.IsNullOrWhiteSpace(status.Output))
            throw new InvalidOperationException("Worktree has unresolved modifications; inspect and clean manually");

        return store.Change(s =>
        {
            var j = s.Jobs.Single(j => j.Id == id);
            if (j.State != JobStates.Blocked || j.Stage != "reconcile" ||
                j.ExecutionGeneration != snapshot.ExecutionGeneration ||
                j.WorkerProcessId != snapshot.WorkerProcessId ||
                j.CheckpointStage != snapshot.CheckpointStage ||
                j.CheckpointCommit != snapshot.CheckpointCommit ||
                j.Worktree != snapshot.Worktree || !string.IsNullOrEmpty(j.LeaseOwner))
                throw new InvalidOperationException("Recovery state changed while reconciling");
            j.State = JobStates.Queued;
            j.Stage = "reconciled";
            j.Error = "";
            j.UpdatedAt = DateTimeOffset.UtcNow;
            s.Events.Add(new FactoryEvent { JobId = id, Type = "reconciled",
                Message = "Reviewed clean checkpoint " + j.CheckpointCommit + "; " + reason.Trim()[..Math.Min(reason.Trim().Length, 1000)] });
            return j;
        });
    }
}
