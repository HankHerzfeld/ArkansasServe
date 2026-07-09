using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace ArkansasServe.Functions.Services;

/// <summary>
/// Small transient-fault retry for the inline service-log side effects (pending-approval
/// and notification writes). The Cosmos SDK already retries throttling (429) on its own;
/// this adds a few short retries for other transient faults (503/408/timeouts) so a single
/// blip doesn't drop a side effect. Terminal failures still surface to the caller — the
/// pending-approval queue is additionally self-healed by reconciliation on read.
/// </summary>
internal static class CosmosRetry
{
    public static async Task ExecuteAsync(Func<Task> operation, ILogger logger, string operationName, int maxAttempts = 3)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await operation();
                return;
            }
            catch (CosmosException ex) when (IsTransient(ex.StatusCode) && attempt < maxAttempts)
            {
                var delay = TimeSpan.FromMilliseconds(200 * attempt);
                logger.LogWarning(ex,
                    "Transient Cosmos failure on {Operation} (attempt {Attempt}/{Max}, status {Status}); retrying in {Delay}ms",
                    operationName, attempt, maxAttempts, (int)ex.StatusCode, delay.TotalMilliseconds);
                await Task.Delay(delay);
            }
        }
    }

    private static bool IsTransient(HttpStatusCode status) => status is
        HttpStatusCode.TooManyRequests
        or HttpStatusCode.ServiceUnavailable
        or HttpStatusCode.RequestTimeout
        or HttpStatusCode.GatewayTimeout;
}
