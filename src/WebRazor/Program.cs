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

            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddSingleton<SessionTokenStore>();
            builder.Services.AddScoped<AdminLoginService>();
            builder.Services.AddTransient<ApiAuthHandler>();

            // Raw client: login and refresh, where there is no session token to attach yet.
            builder.Services.AddHttpClient(ApiAuthHandler.RawClientName, ConfigureApiClient);
            builder.Services.AddHttpClient<TradePulseApiClient>(ConfigureApiClient)
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
                options.Conventions.AllowAnonymousToPage("/Error");
            });

            var app = builder.Build();

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
