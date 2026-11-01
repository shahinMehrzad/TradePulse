using Dapper;
using Domain.Entities;
using Npgsql;

namespace Trader.Audit;

/// <summary>
/// Writes to the same audit log the panel shows (<c>config.audit_log</c>, made by the API's migrations). Like the API's own audit trail,
/// a failure to write is logged and never thrown: the key really was added or removed. What is written is the label and what happened,
/// never a key, a public key or the API key.
/// </summary>
public sealed class AuditWriter(NpgsqlDataSource dataSource, TimeProvider time, ILogger<AuditWriter> logger)
{
    public async Task RecordAsync(AuditAction action, string userName, string label, string details, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO config.audit_log ("OccurredAt", "UserName", "Action", "EntityType", "EntityId", "Summary", "Details")
                VALUES (@at, @userName, @action, @type, 0, @summary, @details)
                """,
                new
                {
                    at = DateTime.SpecifyKind(time.GetUtcNow().UtcDateTime, DateTimeKind.Unspecified), // timestamp without zone, always UTC
                    userName = Truncate(userName, 256),
                    action = action.ToString(),
                    type = AuditEntityTypes.ExchangeKey,
                    summary = Truncate(label, 256),
                    details
                }, cancellationToken: cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not write the audit log entry for an exchange key ({Action}).", action);
        }
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
