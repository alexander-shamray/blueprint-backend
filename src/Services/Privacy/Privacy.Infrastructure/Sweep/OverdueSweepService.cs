using System.Data;
using Common.Application;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Privacy.Application.ErasureRequests.MarkOverdue;

namespace Privacy.Infrastructure.Sweep;

/// <summary>Finds the open requests that have outlived their due time and marks each overdue (ADR-092).</summary>
/// <remarks>Nothing closes an overdue request: an operator reissues it. This only makes the silence visible.</remarks>
public sealed class OverdueSweepService : BackgroundService
{
    /// <summary>A due time is measured in days, so a minute is prompt and a pass costs one indexed read.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>Requests per pass, so a backlog drains over a few passes and a pass still ends.</summary>
    public const int BatchSize = 100;

    private const string DueSql =
        """
        SELECT TOP (@BatchSize) RequestId
        FROM privacy.ErasureRequests
        WHERE Status = 'Open' AND DueAt <= @Now
        ORDER BY DueAt;
        """;

    private static readonly Action<ILogger, Exception?> PassFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(1, nameof(PassFailed)),
            "The overdue sweep failed; retrying next pass.");

    private static readonly Action<ILogger, Guid, string, Exception?> Refused =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Error,
            new EventId(2, nameof(Refused)),
            "Request {RequestId} could not be marked overdue: {ErrorCode}.");

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly ILogger<OverdueSweepService> _log;

    public OverdueSweepService(IServiceScopeFactory scopes, TimeProvider clock, ILogger<OverdueSweepService> log)
    {
        _scopes = scopes;
        _clock = clock;
        _log = log;
    }

    // stoppingToken, not ct: CA1725 keeps the base's name, an error under ADR-019.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval);

        // A pass at start, so a host restarted more often than the interval still sweeps.
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                PassFailed(_log, exception);
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    /// <summary>One pass: the requests now overdue, each marked in a command of its own. Public for the suite.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
        IServiceProvider services = scope.ServiceProvider;

        Guid[] due;
        using (IDbConnection connection = services.GetRequiredService<IDbConnectionFactory>().Create())
        {
            due =
            [
                .. await connection.QueryAsync<Guid>(
                    new CommandDefinition(
                        DueSql,
                        new { BatchSize, Now = _clock.GetUtcNow() },
                        cancellationToken: ct))
            ];
        }

        IDispatcher dispatcher = services.GetRequiredService<IDispatcher>();
        int marked = 0;

        foreach (Guid id in due)
        {
            // One aggregate per transaction (§2.3), and a request that fails does not stop the others.
            Result result = await dispatcher.SendAsync(new MarkErasureRequestOverdueCommand(id), ct);

            if (result.IsFailure)
                Refused(_log, id, result.Error.Code, null);
            else
                marked++;
        }

        return marked;
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
