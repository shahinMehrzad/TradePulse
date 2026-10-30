using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using WebRazor.Api;

namespace WebRazor
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddOptions<ApiOptions>()
                .BindConfiguration(ApiOptions.SectionName)
                .Validate(o => Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out _), "Api:BaseUrl must be an absolute URL.")
                .ValidateOnStart();

            // Keys of data protection (the sign-in cookies, antiforgery) survive a restart when a folder is given; the deploy stack mounts a volume there.
            var dataProtection = builder.Services.AddDataProtection().SetApplicationName("TradePulse.Panel");
            if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
                dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
            builder.Services.AddProxyHeaders();
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddSingleton<SessionTokenStore>();
            builder.Services.AddScoped<AdminLoginService>();
            builder.Services.AddSingleton<PendingLoginCookie>();
            builder.Services.AddTransient<ApiAuthHandler>();

            // Raw client: login and refresh, where there is no session token to attach yet.
            builder.Services.AddHttpClient(ApiAuthHandler.RawClientName, ConfigureApiClient);
            builder.Services.AddHttpClient<TradePulseApiClient>(ConfigureApiClient)
                .AddHttpMessageHandler<ApiAuthHandler>();

            // The key vault (the Trader program) is optional: without Trader:BaseUrl the Exchange keys page says so and the menu does not show it.
            builder.Services.Configure<TraderOptions>(builder.Configuration.GetSection(TraderOptions.SectionName));
            builder.Services.AddHttpClient<TraderApiClient>((services, client) =>
                {
                    var trader = services.GetRequiredService<IOptions<TraderOptions>>().Value;
                    if (trader.Enabled)
                        client.BaseAddress = new Uri(trader.BaseUrl.TrimEnd('/') + "/");
                    client.Timeout = TimeSpan.FromSeconds(60); // making a 4096-bit key pair and asking Binance take a few seconds
                })
                .AddHttpMessageHandler<ApiAuthHandler>();

            // The browser only ever holds this cookie; the API tokens stay in SessionTokenStore on the server.
            builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(options =>
                {
                    options.LoginPath = "/Login";
                    options.AccessDeniedPath = "/Login";
                    options.Cookie.Name = "tradepulse.panel";
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SameSite = SameSiteMode.Strict;
                    // Plain http is only tolerated in Development (the http launch profile).
                    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
                        ? CookieSecurePolicy.SameAsRequest
                        : CookieSecurePolicy.Always;
                    options.ExpireTimeSpan = SessionTokenStore.SessionLifetime;
                    options.SlidingExpiration = false;
                    options.Events.OnValidatePrincipal = async context =>
                    {
                        // A restart, a logout elsewhere or a failed token refresh empties the store: sign the cookie out too.
                        var sessionId = context.Principal?.FindFirst(ApiAuthHandler.SessionClaim)?.Value;
                        var store = context.HttpContext.RequestServices.GetRequiredService<SessionTokenStore>();
                        if (sessionId is null || !store.TryGet(sessionId, out _))
                        {
                            context.RejectPrincipal();
                            await context.HttpContext.SignOutAsync();
                        }
                    };
                });

            builder.Services.AddAuthorizationBuilder()
                .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser().Build());

            builder.Services.AddRazorPages(options =>
            {
                options.Conventions.AllowAnonymousToPage("/Login");
                options.Conventions.AllowAnonymousToPage("/LoginTwoFactor");
                options.Conventions.AllowAnonymousToPage("/LoginSetup");
                options.Conventions.AllowAnonymousToPage("/Error");
            });

            var app = builder.Build();

            // First, so that the scheme and the client address are the visitor's, not the proxy's.
            app.UseForwardedHeaders();

            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.Use(async (context, next) =>
            {
                var headers = context.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                headers.ContentSecurityPolicy =
                    "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; form-action 'self'";
                await next();
            });

            app.UseHttpsRedirection();

            app.UseRouting();

            app.UseAuthentication();
            app.UseAuthorization();

            // Static files must stay public, or the login page itself would have no CSS (the fallback policy covers them too).
            app.MapStaticAssets().AllowAnonymous();
            app.MapRazorPages()
               .WithStaticAssets();

            app.Run();
        }

        private static void ConfigureApiClient(IServiceProvider services, HttpClient client)
        {
            client.BaseAddress = new Uri(services.GetRequiredService<IOptions<ApiOptions>>().Value.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(15);
        }
    }
}
