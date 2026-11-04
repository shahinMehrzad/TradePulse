using System.Net;
using System.Net.Http.Json;
using Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace WebApi.IntegrationTests;

/// <summary>
/// A user that does not exist must cost as much time as one that does, or the time alone says which names exist. Time is a poor thing to measure
/// in a test, so these count what the time is made of: the password hash that is compared. Every answer to a password, whoever it is for and
/// whatever came of it, compares exactly one.
/// </summary>
public class LoginTimingTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class CountingHasher : IPasswordHasher<ApplicationUser>
    {
        private readonly PasswordHasher<ApplicationUser> _inner = new();
        private int _compared;

        public int Compared => Volatile.Read(ref _compared);

        public string HashPassword(ApplicationUser user, string password) => _inner.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(ApplicationUser user, string hashedPassword, string providedPassword)
        {
            Interlocked.Increment(ref _compared);
            return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }

    private sealed class RemoteAddressFilter(IPAddress remote) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, continuation) =>
            {
                context.Connection.RemoteIpAddress = remote;
                return continuation();
            });
            next(app);
        };
    }

    private (HttpClient Client, CountingHasher Hasher) Visitor()
    {
        var hasher = new CountingHasher();
        var host = api.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new RemoteAddressFilter(IPAddress.Parse("203.0.113.77"))));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPasswordHasher<ApplicationUser>>();
                services.AddSingleton<IPasswordHasher<ApplicationUser>>(hasher);
            });
        });
        return (host.CreateClient(), hasher);
    }

    private static async Task<HttpStatusCode> LoginAsync(HttpClient client, string userName, string password) =>
        (await client.PostAsJsonAsync("/api/auth/login", new { Email = "", Username = userName, Password = password }, Ct)).StatusCode;

    [Fact]
    public async Task AUserThatDoesNotExistCostsOneHashLikeAWrongPassword()
    {
        var account = await api.SetUpAdministratorAsync("timing", Ct);
        var (client, hasher) = Visitor();

        var before = hasher.Compared;
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(client, $"nobody{Guid.NewGuid():N}", "Whatever-Password-1!"));
        var unknown = hasher.Compared - before;

        before = hasher.Compared;
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(client, account.UserName, "Whatever-Password-1!"));
        var known = hasher.Compared - before;

        Assert.Equal(1, unknown);
        Assert.Equal(known, unknown);
    }

    [Fact]
    public async Task AnAddressThatIsLockedOutCostsOneHashToo()
    {
        var account = await api.SetUpAdministratorAsync("timinglock", Ct);
        var (client, hasher) = Visitor();
        for (var i = 0; i < 5; i++)
            await LoginAsync(client, account.UserName, "Whatever-Password-1!");

        var before = hasher.Compared;
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(client, account.UserName, ApiFixture.NewUserPassword));   // locked: not even compared
        var locked = hasher.Compared - before;

        Assert.Equal(1, locked);   // a stand-in is compared, so a lock does not answer faster than a wrong password
    }

    [Fact]
    public async Task ARightPasswordCostsOneHashToo()
    {
        var account = await api.SetUpAdministratorAsync("timingright", Ct);
        var (client, hasher) = Visitor();

        var before = hasher.Compared;
        Assert.Equal(HttpStatusCode.OK, await LoginAsync(client, account.UserName, ApiFixture.NewUserPassword));

        Assert.Equal(1, hasher.Compared - before);
    }
}
