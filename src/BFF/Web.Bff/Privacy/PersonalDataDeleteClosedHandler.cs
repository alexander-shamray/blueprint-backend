using Common.Application;
using Common.Contracts.Privacy.V1;
using Dapper;
using Web.Bff.Persistence;

namespace Web.Bff.Privacy;

/// <summary>Takes Privacy's close of a request onto the BFF's own audit row (ADR-139).</summary>
/// <remarks>Public, because §6.2's scan is public-only. The first close stands, and a request with no row here
/// is left as it is: the row is written by the request, never by its close.</remarks>
public sealed class PersonalDataDeleteClosedHandler(IDbConnectionFactory connections)
    : IIntegrationEventHandler<PersonalDataDeleteClosed>
{
    private const string CloseSql =
        """
        UPDATE bff.PersonalDataErasures
        SET ClosedAt = @ClosedAt
        WHERE RequestId = @RequestId
            AND ClosedAt IS NULL;
        """;

    public async Task HandleAsync(PersonalDataDeleteClosed integrationEvent, CancellationToken ct)
    {
        using var connection = connections.Create();

        await connection.ExecuteAsync(
            new CommandDefinition(
                CloseSql,
                new { integrationEvent.RequestId, ClosedAt = integrationEvent.OccurredAt },
                cancellationToken: ct));
    }
}
