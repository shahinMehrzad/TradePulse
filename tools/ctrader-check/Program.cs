using CtraderCheck;

// A tool for the day the cTrader application is approved: get the tokens without a browser plugin, and look at what the broker really does
// before the collector depends on it. It never places an order and never prints a secret (add --print to see a token on purpose).
//
//   dotnet run --project tools/ctrader-check -- token   [--env demo|live] [--scope accounts|trading] [--redirect <uri>] [--account <id>] [--print]
//   dotnet run --project tools/ctrader-check -- refresh [--env demo|live] [--print]
//   dotnet run --project tools/ctrader-check -- check   [--symbol EURUSD] [--days 14]
//
// Client id and secret: CTRADER_CLIENT_ID and CTRADER_CLIENT_SECRET in the environment, or typed when asked (the secret is not shown).

try
{
    var options = CliOptions.Parse(args);
    switch (options.Command)
    {
        case "token":
            return await TokenCommand.RunAsync(options, refresh: false);
        case "refresh":
            return await TokenCommand.RunAsync(options, refresh: true);
        case "check":
            return await CheckCommand.RunAsync(options);
        default:
            Console.WriteLine("Usage: ctrader-check token | refresh | check   (see the top of Program.cs)");
            return 2;
    }
}
catch (Exception ex) when (ex is Infrastructure.Exchange.Ctrader.CtraderException or ArgumentException or InvalidOperationException or IOException)
{
    // The messages of these never hold a secret (the adapter makes sure of that).
    Console.Error.WriteLine($"Failed: {ex.Message}");
    return 1;
}
