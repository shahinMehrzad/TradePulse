using Infrastructure;
using Infrastructure.Exchange.Binance;
using Infrastructure.Notifications;
using SignalReceiver;

// Takes the signals of the program that makes them (a webhook on the internal network of the stack: it has no public port), opens a paper trade
// for each one that is worth taking, follows the price of the open trades until the target or the stop is reached, and tells the chat. Its
// own process and its own database role (tp_signals), so nothing it does can slow the collector.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructureServices(builder.Configuration);
builder.Services.AddBinanceExchange(builder.Configuration);
builder.Services.AddTradeChat(builder.Configuration);
builder.Services.AddSignalReceiver(builder.Configuration);

var app = builder.Build();

app.MapSignals();

app.Run();

// Lets WebApplicationFactory<Program> reach the entry point from the integration tests.
public partial class Program;
