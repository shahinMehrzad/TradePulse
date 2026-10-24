using Application.Dtos.CandleWorkerConfigurations;
using Application.Interfaces;
using Domain.Entities;
using Infrastructure.Identity;
using Domain.Repositories;
using Infrastructure.Configurations;
using Infrastructure.Persistence;
using Infrastructure.ReadModels;
using Infrastructure.Repositories;
using Infrastructure.Repositories.TimeSeries;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

        services.AddScoped<ICandleRepository>(provider => new CandleRepository(connectionString));
        services.AddScoped<IMarketDataRepository<FundingRate>>(_ => new TimeSeriesRepository<FundingRate>(connectionString, new FundingRateTable()));
        services.AddScoped<IMarketDataRepository<OpenInterest>>(_ => new TimeSeriesRepository<OpenInterest>(connectionString, new OpenInterestTable()));
        services.AddScoped<IMarketDataRepository<Liquidation>>(_ => new TimeSeriesRepository<Liquidation>(connectionString, new LiquidationTable()));
        services.AddScoped<ILookupsRepository, LookupsRepository>();
        services.AddScoped<ICandleWorkerConfigurationRepository, CandleWorkerConfigurationRepository>();
        services.AddScoped<ISymbolDataConfigurationRepository, SymbolDataConfigurationRepository>();
        services.AddScoped<IReadModel<CandleWorkerConfigurationListItem>, CandleWorkerConfigurationReadModel>();

        // The config schema (series the admin chose) is read by the worker and written by WebApi, so it lives here, not with auth.
        // ConfigConnection is optional: the schema normally sits in the same database as everything else.
        var configConnectionString = configuration.GetConnectionString("ConfigConnection") ?? connectionString;
        services.AddDbContext<ConfigDbContext>(options =>
            options.UseNpgsql(configConnectionString, npgsqlOptions =>
            {
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "config");
            }));

        services.Configure<JwtConfigurations>(configuration.GetSection(nameof(JwtConfigurations)));

        return services;
    }

    public static IServiceCollection AddAuthInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<AuthDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("AuthConnection"), npgsqlOptions =>
            {
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "auth");
            }));

        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();

        services.AddIdentityCore<ApplicationUser>()
            .AddRoles<ApplicationRole>()
            .AddSignInManager<SignInManager<ApplicationUser>>()
            .AddEntityFrameworkStores<AuthDbContext>()
            .AddDefaultTokenProviders();

        services.AddScoped<IIdentityService, IdentityService>();
        services.AddSingleton<IAccessTokenService, JwtAccessTokenService>();

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
            var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
            var roleManager = services.GetRequiredService<RoleManager<ApplicationRole>>();

            await context.Database.MigrateAsync();
            await contextConfig.Database.MigrateAsync();

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
