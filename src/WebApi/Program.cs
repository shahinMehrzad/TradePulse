using Infrastructure;
using Application;
using WebApi.Endpoints;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.AddDataProtection();
builder.Services.AddApplicationServices();

// Candle reads go through a HybridCache (memory, plus Redis when a connection string is configured).
if (builder.Configuration.GetValue("CandleCache:Enabled", true))
{
    var redis = builder.Configuration.GetConnectionString("Redis");
    if (!string.IsNullOrWhiteSpace(redis))
        builder.Services.AddStackExchangeRedisCache(options => options.Configuration = redis);

    builder.Services.AddCandleCaching(builder.Configuration);
}

builder.Services.AddAuthInfrastructure(builder.Configuration);
builder.Services.AddInfrastructureServices(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

await Infrastructure.DependencyInjection.SeedAsync(app.Services, app.Configuration);

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();


app.MapControllers();
app.MapCandleEndpoints();

app.Run();
