using Infrastructure;
using Application;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.AddDataProtection();
builder.Services.AddApplicationServices();
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

app.Run();
