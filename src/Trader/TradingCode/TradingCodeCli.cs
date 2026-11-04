namespace Trader.TradingCode;

/// <summary>
/// The last way back for a user who lost the phone and the recovery codes, for somebody with a shell on the server (the same trust as the
/// master key, and no more): <c>dotnet Trader.dll reset-trading-code &lt;user id&gt;</c> forgets the code, and the user sets a new one up.
/// <c>list-trading-codes</c> shows whose codes exist. The ids are the ones of <c>auth.users</c>
/// (<c>select "Id", "UserName" from auth.users</c> in the main database).
/// </summary>
public static class TradingCodeCli
{
    public static async Task<int> ListAsync(IServiceProvider services)
    {
        foreach (var (owner, enrolled, since) in await services.GetRequiredService<TradingCodeRepository>().ListAsync(CancellationToken.None))
            Console.WriteLine($"{owner}  {(enrolled ? $"set up {since:yyyy-MM-dd HH:mm} UTC" : "not finished")}");
        return 0;
    }

    public static async Task<int> ResetAsync(IServiceProvider services, string userId)
    {
        if (!Guid.TryParse(userId, out var owner))
        {
            Console.Error.WriteLine("Give the id of the user (a GUID). `list-trading-codes` shows the ones that have a code.");
            return 2;
        }

        var codes = services.GetRequiredService<TradingCodeRepository>();
        if (!await codes.DeleteAsync(owner, CancellationToken.None))
        {
            Console.Error.WriteLine("That user has no trading code.");
            return 1;
        }

        // Nothing runs the queue of alerts in this mode: what the reset raises is sent here.
        await services.GetRequiredService<TradingCodeService>().ResetAsync(owner, owner.ToString(), "from the server console", CancellationToken.None);
        var queue = services.GetRequiredService<Trader.Audit.SecurityAlerts>();
        var sender = services.GetRequiredService<Trader.Audit.SecurityAlertSender>();
        while (queue.Reader.TryRead(out var alert))
            await sender.SendNowAsync(alert, CancellationToken.None);

        Console.WriteLine($"The trading code of {owner} was forgotten. They set up a new one the next time they use the keys or the orders.");
        return 0;
    }
}
