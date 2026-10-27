using BackgroundWorker;
using BackgroundWorker.Health;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateDefaultBuilder(args);

var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json")
    .AddEnvironmentVariables()
    .Build();

var health = configuration.GetSection("Health").Get<HealthConfigurations>() ?? new HealthConfigurations();

if (health.Enabled)
{
    // Small Kestrel host that only serves /health, so orchestrators and humans can ask whether the worker is OK.
    builder.ConfigureWebHost(web => web
        .UseKestrel()
        .UseUrls(health.Url)
        .ConfigureServices(services => services.AddRouting())
        .Configure(app => app.UseHealthEndpoints()));
}

var host = builder.ConfigureServices((context, services) => services.AddCollector(context.Configuration)).Build();

await host.RunAsync();
