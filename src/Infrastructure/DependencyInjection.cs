using Application.Dtos.AuditLog;
using Application.Dtos.CandleWorkerConfigurations;
using Application.Interfaces;
using Application.Research;
using Application.Research.Watch;
using Domain.Entities;
using Infrastructure.Identity;
using Domain.Repositories;
using Infrastructure.Configurations;
using Infrastructure.Monitoring;
using Infrastructure.Notifications;
using Infrastructure.Persistence;
using Infrastructure.ReadModels;
using Infrastructure.Repositories;
using Infrastructure.Repositories.TimeSeries;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.RequiredConnectionString("DefaultConnection");

        services.AddScoped<ICandleRepository>(provider => new CandleRepository(connectionString));
        services.AddScoped<IMarketDataRepository<FundingRate>>(_ => new TimeSeriesRepository<FundingRate>(connectionString, new FundingRateTable()));
        services.AddScoped<IMarketDataRepository<OpenInterest>>(_ => new TimeSeriesRepository<OpenInterest>(connectionString, new OpenInterestTable()));
        services.AddScoped<IMarketDataRepository<LongShortRatio>>(_ => new TimeSeriesRepository<LongShortRatio>(connectionString, new LongShortRatioTable()));
        services.AddScoped<IMarketDataRepository<TakerVolume>>(_ => new TimeSeriesRepository<TakerVolume>(connectionString, new TakerVolumeTable()));
        services.AddScoped<IMarketDataRepository<Liquidation>>(_ => new TimeSeriesRepository<Liquidation>(connectionString, new LiquidationTable()));
        // Read back for the dashboard only; the collector never asks for these.
        services.AddScoped<ITimeSeriesReader<LongShortRatio>>(_ => new TimeSeriesReader<LongShortRatio>(connectionString, new LongShortRatioTable()));
        services.AddScoped<ITimeSeriesReader<TakerVolume>>(_ => new TimeSeriesReader<TakerVolume>(connectionString, new TakerVolumeTable()));
        services.AddScoped<ILookupsRepository, LookupsRepository>();
        services.AddScoped<ISymbolCatalogRepository, SymbolCatalogRepository>();
        services.AddScoped<ICandleWorkerConfigurationRepository, CandleWorkerConfigurationRepository>();
        services.AddScoped<ISymbolDataConfigurationRepository, SymbolDataConfigurationRepository>();
        services.AddScoped<IReadModel<CandleWorkerConfigurationListItem>, CandleWorkerConfigurationReadModel>();
        services.AddScoped<IAuditLogRepository, AuditLogRepository>();
        services.AddScoped<IReadModel<AuditLogListItem>, AuditLogReadModel>();
        services.AddScoped<IMarketEventStore, MarketEventStore>();
        services.AddScoped<IShockAlertStore, ShockAlertStore>();
        services.AddScoped<IShockWatchConfigurationRepository, ShockWatchConfigurationRepository>();
        services.AddScoped<IWatchedSymbolSource, ConfiguredWatchedSymbols>();

        // Work without a signed-in user (the collector) is done by "system"; the API registers the user of the request after this.
        services.TryAddScoped<ICurrentUser, SystemUser>();

        // The config schema (series the admin chose) is read by the worker and written by WebApi, so it lives here, not with auth.
        // ConfigConnection is optional: the schema normally sits in the same database as everything else.
        var configConnectionString = configuration.GetConnectionString("ConfigConnection") ?? connectionString;
        services.AddSingleton<ITradedSymbolSource>(_ => new TradedSymbolSource(configConnectionString));
        services.AddDbContext<ConfigDbContext>(options =>
            options.UseNpgsql(configConnectionString, npgsqlOptions =>
            {
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "config");
            }));

        // Like ConfigConnection, ResearchConnection is optional: the research schema normally sits in the same database as everything else.
        var researchConnectionString = configuration.GetConnectionString("ResearchConnection") ?? connectionString;
        services.AddDbContext<ResearchDbContext>(options =>
            options.UseNpgsql(researchConnectionString, npgsqlOptions =>
            {
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "research");
            }));

        // The signals schema too: the same database unless SignalsConnection says otherwise. The API makes it (its migrations), the service
        // that takes signals writes it with a role of its own.
        var signalsConnectionString = configuration.GetConnectionString("SignalsConnection") ?? connectionString;
        services.AddDbContext<SignalsDbContext>(options =>
            options.UseNpgsql(signalsConnectionString, npgsqlOptions =>
            {
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "signals");
            }));
        services.AddScoped<Application.Signals.ISignalStore, SignalStore>();

        services.Configure<JwtConfigurations>(configuration.GetSection(nameof(JwtConfigurations)));

        return services;
    }

    public static IServiceCollection AddCollectorHealthClient(this IServiceCollection services)
    {
        services.AddHttpClient<ICollectorHealthSource, CollectorHealthSource>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5);
            client.MaxResponseContentBufferSize = 256 * 1024;
        });
        return services;
    }
    
    public static string RequiredConnectionString(this IConfiguration configuration, string name) =>
        configuration.GetConnectionString(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"Connection string '{name}' is not set. It is not in appsettings.json on purpose: give it as a user secret " +
                $"(dotnet user-secrets set \"ConnectionStrings:{name}\" \"Host=...;Database=...;Username=...;Password=...\" --project <the project>) " +
                $"or as the environment variable ConnectionStrings__{name}.");

    public static IServiceCollection AddAuthInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var authConnection = configuration.RequiredConnectionString("AuthConnection");

        services.AddDbContext<AuthDbContext>(options =>
            options.UseNpgsql(authConnection, npgsqlOptions =>
            {
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "auth");
            }));

        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();

        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.Password.RequiredLength = 12;
                options.User.RequireUniqueEmail = true;
                // Identity's lockout is only what disables an account (a lockout that never ends). The wrong guesses are not counted by it but
                // by LoginGuard: per address for passwords, per account for codes, with locks that grow (see LoginThrottlePolicy).
            })
            .AddRoles<ApplicationRole>()
            .AddSignInManager<SignInManager<ApplicationUser>>()
            .AddEntityFrameworkStores<AuthDbContext>()
            // Registered after the EF stores so it replaces their user store: second-factor secrets are not kept as plain text.
            .AddUserStore<ProtectedUserStore>()
            .AddDefaultTokenProviders();

        services.AddScoped<IIdentityService, IdentityService>();
        services.AddScoped<ILoginThrottle, LoginThrottleService>();
        services.AddScoped<ITwoFactorReplayGuard, TwoFactorReplayGuard>();
        // Where a request comes from, for the wrong passwords held against an address; the API replaces this with the visitor's address.
        services.TryAddScoped<IClientAddress, LocalClientAddress>();

        // Alerts about somebody trying to get in, to a bot of their own. Without its settings they are only written to the audit log.
        services.Configure<SecurityAlertsOptions>(configuration.GetSection(SecurityAlertsOptions.SectionName));
        services.AddSingleton<SecurityAlertQueue>();
        services.AddSingleton<Application.Notifications.ISecurityAlerts>(provider => provider.GetRequiredService<SecurityAlertQueue>());
        services.AddHttpClient(SecurityAlertDispatcher.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://api.telegram.org/");
            client.Timeout = TimeSpan.FromSeconds(15);
        }).RemoveAllLoggers(); // the bot token is part of the address
        services.AddHostedService<SecurityAlertDispatcher>();
        services.AddScoped<IUserAdministration, UserAdministration>();
        services.Configure<Notifications.MailOptions>(configuration.GetSection(Notifications.MailOptions.SectionName));
        services.AddSingleton<IMailSender, Notifications.SmtpMailSender>();
        services.AddSingleton<IAccessTokenService, JwtAccessTokenService>();
        services.AddSingleton<IChallengeTokenService, JwtChallengeTokenService>();
        services.AddSingleton<IElevationTokenService, JwtElevationTokenService>();
        services.TryAddSingleton(TimeProvider.System);

        return services;
    }

    public static async Task SeedAsync(IServiceProvider serviceProvider, IConfiguration configuration)
    {
        using var scope = serviceProvider.CreateScope();
        var services = scope.ServiceProvider;

        try
        {
            var context = services.GetRequiredService<AuthDbContext>();
            var contextConfig = services.GetRequiredService<ConfigDbContext>();
            var contextResearch = services.GetRequiredService<ResearchDbContext>();
            var contextSignals = services.GetRequiredService<SignalsDbContext>();
            var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
            var roleManager = services.GetRequiredService<RoleManager<ApplicationRole>>();

            await context.Database.MigrateAsync();
            await contextConfig.Database.MigrateAsync();
            await contextResearch.Database.MigrateAsync();
            await contextSignals.Database.MigrateAsync();

            var converted = await UserTokenProtection.ProtectStoredAsync(context,
                services.GetRequiredService<IDataProtectionProvider>(), services.GetRequiredService<IPasswordHasher<ApplicationUser>>(), CancellationToken.None);
            if (converted > 0)
                Console.WriteLine($"Protected {converted} two-factor secret(s) that were stored as plain text.");

            string? adminRoleName = configuration["AdminConfigurations:Role"];
            if (string.IsNullOrEmpty(adminRoleName))
            {
                Console.WriteLine("Error: No admin role name specified in configuration.");
                return;
            }            

            if (!await roleManager.RoleExistsAsync(adminRoleName))
                await roleManager.CreateAsync(new ApplicationRole { Name = adminRoleName });

            var adminEmail = configuration["AdminConfigurations:Email"];
            var adminUserName = configuration["AdminConfigurations:UserName"] ?? "admin";
            var adminPassword = configuration["AdminConfigurations:Password"];

            if (!string.IsNullOrEmpty(adminEmail) && !string.IsNullOrEmpty(adminPassword))
            {
                var existingUser = await userManager.FindByEmailAsync(adminEmail);
                if (existingUser == null)
                {
                    var adminUser = new ApplicationUser
                    {
                        UserName = adminUserName,
                        Email = adminEmail,
                        EmailConfirmed = true
                    };

                    var result = await userManager.CreateAsync(adminUser, adminPassword);
                    if (result.Succeeded)
                    {
                        await userManager.AddToRoleAsync(adminUser, adminRoleName);
                    }
                }
            }
            else
            {
                Console.WriteLine("Error: Admin email or password is not specified in configuration.");
            }
        }
        catch (Exception ex)
        {            
            Console.WriteLine("Error: An error occurred while migrating or seeding the database.");
            Console.WriteLine(ex.Message);
        }
    }
}
