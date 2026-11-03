using Npgsql;

namespace Trader.Audit;

/// <summary>
/// The stack's main database, where this program only writes the audit log (<c>config.audit_log</c>) and publishes the copy of the
/// enabled symbols (<c>config.traded_symbols</c>). Keys, orders and limits are not there: they are in the vault's own database.
/// Without <c>ConnectionStrings:Main</c> it is the vault database itself (development, tests, a single-database setup).
/// </summary>
public sealed class MainDatabase(NpgsqlDataSource source)
{
    public NpgsqlDataSource Source { get; } = source;
}
