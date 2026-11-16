using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Factory;

internal static class SettingsFile
{
    internal static void Save(string path, FactorySettings settings)
    {
        var contents = Json.Stringify(settings);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var ownsTemporary = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsTemporary = true;
                using var writer = new StreamWriter(stream);
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            ownsTemporary = false;
        }
        finally
        {
            if (ownsTemporary) File.Delete(temporary);
        }
    }
}

internal sealed record SettingsSaveResult(RuntimeSettingsRevision Revision, string? AuditWarning);

internal static class DashboardSettings
{
    internal static SettingsSaveResult Save(StateStore store, FactorySettings settings, SettingsInput input, ILogger logger)
    {
        string? auditWarning = null;
        var revision = settings.CommitRuntime(current => Validate(current, settings.AutoMerge, input),
            candidate => SettingsFile.Save(Path.Combine(store.Home, "settings.json"), candidate),
            committed =>
            {
                SlottedAgentRuntime.NotifyLimitChanged(settings);
                var id = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(committed, Json.Options)));
                try
                {
                    store.Event(null, "settings_updated", "Mission Control committed runtime settings revision " + id);
                }
                catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
                {
                    logger.LogError(error, "Runtime settings revision {Revision} was saved and applied, but its audit event failed", id);
                    auditWarning = "Settings were saved and applied, but the audit event could not be recorded.";
                }
            });
        return new(revision, auditWarning);
    }

    internal static object Describe(FactorySettings settings, RuntimeSettingsRevision revision, string? auditWarning = null) => new
    {
        revision.MaxParallel, revision.MaxAgentSessions, revision.MaxReviewRounds,
        revision.LeaseSeconds, revision.PollSeconds, revision.AllowPush, settings.AutoMerge, settings.Runtime,
        revision.GoalReservationTokens, revision.GoalExtensionIncrementTokens,
        revision.GoalExtensionThresholdTokens, revision.MaxJobReservationTokens, AuditWarning = auditWarning
    };

    private static RuntimeSettingsRevision Validate(RuntimeSettingsRevision current, bool autoMerge, SettingsInput input)
    {
        if (input.MaxParallel is < 1 or > 32 || input.MaxAgentSessions is < 1 or > 64 ||
            input.MaxReviewRounds is < 1 or > 10 || input.LeaseSeconds is < 30 or > 3600 ||
            input.PollSeconds is < 1 or > 300 ||
            input.GoalReservationTokens is < 256 or > 65536 ||
            input.GoalExtensionIncrementTokens is < 256 or > 65536 ||
            input.GoalExtensionThresholdTokens is < 0 or > 65536 ||
            input.MaxJobReservationTokens is < 256 or > 1000000)
            throw new ArgumentException("Settings out of range");

        var initial = input.GoalReservationTokens ?? current.GoalReservationTokens;
        var max = input.MaxJobReservationTokens ?? current.MaxJobReservationTokens;
        if (max < initial)
            throw new ArgumentException("Per-job reservation limit must be >= initial allocation");
        if (input.AutoMerge && !autoMerge)
            throw new ArgumentException("Automatic merging is not supported; merge through a manually approved PR");

        return current with
        {
            MaxParallel = input.MaxParallel,
            MaxAgentSessions = input.MaxAgentSessions,
            MaxReviewRounds = input.MaxReviewRounds,
            LeaseSeconds = input.LeaseSeconds,
            PollSeconds = input.PollSeconds,
            AllowPush = input.AllowPush,
            GoalReservationTokens = initial,
            MaxJobReservationTokens = max,
            GoalExtensionIncrementTokens = input.GoalExtensionIncrementTokens ?? current.GoalExtensionIncrementTokens,
            GoalExtensionThresholdTokens = input.GoalExtensionThresholdTokens ?? current.GoalExtensionThresholdTokens
        };
    }
}
