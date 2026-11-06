using System.Security.Cryptography;
using System.Text;

namespace Factory;

/// <summary>
/// Separates the administrator API from worker-only endpoints. Worker credentials
/// are derived per worker, so a token for one worker cannot impersonate another.
/// </summary>
public static class WorkerAuthentication
{
    public const string WorkerIdHeader = "X-Factory-Worker-Id";

    private static readonly string[] WorkerEndpoints =
    [
        "/api/worker/register", "/api/worker/claim",
        "/api/worker/heartbeat", "/api/worker/complete", "/api/worker/budget/extend", "/api/worker/turn"
    ];

    private static bool IsAsciiLetterOrDigit(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    public static bool IsValidWorkerId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 100 &&
        IsAsciiLetterOrDigit(id[0]) &&
        id.All(c => IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    public static bool IsValidKey(string? key)
    {
        if (key is null || key.Length != 64) return false;
        try { return Convert.FromHexString(key).Length == 32; }
        catch (FormatException) { return false; }
    }

    public static string CreateToken(string workerKey, string workerId)
    {
        if (!IsValidWorkerId(workerId)) throw new ArgumentException("Invalid worker ID", nameof(workerId));
        if (!IsValidKey(workerKey)) throw new InvalidOperationException("Worker token key is missing or invalid");
        var bytes = Encoding.UTF8.GetBytes("factory-worker-v1:" + workerId);
        return Convert.ToHexString(HMACSHA256.HashData(Convert.FromHexString(workerKey), bytes));
    }

    private static bool IsNamespace(string? path, string root) =>
        path is not null &&
        (path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
         path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase));

    public static bool IsWorkerNamespace(string? path) => IsNamespace(path, "/api/worker");

    // Fail closed for unknown worker URLs and HTTP methods; never grant them admin privileges.
    public static bool IsWorkerEndpoint(string method, string? path) =>
        method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
        path is not null && WorkerEndpoints.Contains(path.TrimEnd('/'), StringComparer.OrdinalIgnoreCase);

    public static bool Authorized(string method, string? path, string? bearer, string? workerId,
        FactorySettings settings)
    {
        if (IsNamespace(path, "/api/executor"))
            return string.Equals(path?.TrimEnd('/'), "/api/executor/turn", StringComparison.OrdinalIgnoreCase) &&
                settings.EnableRemoteProviderExecutor && method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                RemoteProviderRouting.ExecutorCredential(settings) is { } executor && WorkerProtocol.Authorized(bearer, executor);
        if (IsWorkerNamespace(path))
            return IsWorkerEndpoint(method, path) && IsValidWorkerId(workerId) &&
                IsValidKey(settings.WorkerTokenKey) &&
                WorkerProtocol.Authorized(bearer, CreateToken(settings.WorkerTokenKey, workerId!));
        return WorkerProtocol.Authorized(bearer, settings.ApiToken);
    }

    public static bool MatchesIdentity(string? authenticatedWorkerId, string? requestWorkerId) =>
        IsValidWorkerId(authenticatedWorkerId) &&
        string.Equals(authenticatedWorkerId, requestWorkerId, StringComparison.Ordinal);
}
