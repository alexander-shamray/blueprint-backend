using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Ordering.TestSupport;

/// <summary>
/// Each async reader or non-query command naming the saga's table or MassTransit's outbox table, with the
/// isolation level of its transaction, so a test reads ADR-112's locks from the statements the host sent.
/// </summary>
public sealed class SagaStatementRecorder : DbCommandInterceptor
{
    private readonly ConcurrentQueue<SagaStatement> _statements = new();

    public IReadOnlyCollection<SagaStatement> Statements => _statements;

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void Record(DbCommand command)
    {
        // MassTransit's table is singular and §9.4's plural, so the bracket is what tells them apart.
        if (command.CommandText.Contains("OrderFulfilmentStates", StringComparison.Ordinal) ||
            command.CommandText.Contains("[OutboxMessage]", StringComparison.Ordinal))
        {
            _statements.Enqueue(new SagaStatement(command.CommandText, command.Transaction?.IsolationLevel));
        }
    }
}

/// <summary>One command's text, and the level of its transaction, or null outside one.</summary>
public sealed record SagaStatement(string Text, IsolationLevel? IsolationLevel);
