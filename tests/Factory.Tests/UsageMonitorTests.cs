using Factory;

internal static class UsageMonitorTests
{
    public static async Task RunAsync()
    {
        var scratch = Directory.CreateTempSubdirectory("factory-allowance-");
        try
        {
            var now = DateTimeOffset.UtcNow;
            var recent = await RefreshFailedAsync("recent", now.AddMinutes(-5));
            if (recent is not { Stale: true, Weekly.RemainingPercent: 75, ShortWindow.RemainingPercent: 60 } ||
                recent.ObservedAt != now.AddMinutes(-5))
                throw new InvalidOperationException("Failed refresh lost or refreshed the previous allowance observation");

            var expired = await RefreshFailedAsync("expired", now.AddMinutes(-16));
            var unknownAge = await RefreshFailedAsync("unknown-age", null);
            foreach (var snapshot in new[] { expired, unknownAge })
                if (snapshot is not { Stale: true, Weekly: null, ShortWindow: null, ObservedAt: null })
                    throw new InvalidOperationException("Unavailable allowance was returned as a usable percentage");
        }
        finally { scratch.Delete(recursive: true); }

        Task<UsageSnapshot> RefreshFailedAsync(string name, DateTimeOffset? observed)
        {
            var home = scratch.CreateSubdirectory(name).FullName;
            var snapshot = new UsageSnapshot
            {
                ObservedAt = observed,
                Weekly = new UsageWindow { UsedPercent = 25, RemainingPercent = 75, WindowMinutes = 10080 },
                ShortWindow = new UsageWindow { UsedPercent = 40, RemainingPercent = 60, WindowMinutes = 300 }
            };
            File.WriteAllText(Path.Combine(home, "usage.json"), Json.Stringify(snapshot));
            return UsageMonitor.GetAsync(home, _ => throw new IOException("Fixture refresh failed"));
        }
    }
}
