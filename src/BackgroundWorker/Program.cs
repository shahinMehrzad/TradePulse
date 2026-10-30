using BackgroundWorker;
using BackgroundWorker.Health;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateDefaultBuilder(args);

// The default builder reads user secrets only in the Development environment, and the collector runs as Production on a developer machine too.
builder.ConfigureAppConfiguration(config => config.AddUserSecrets(typeof(CollectorRegistration).Assembly, optional: true));

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
