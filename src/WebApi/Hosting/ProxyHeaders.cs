using Microsoft.AspNetCore.HttpOverrides;

namespace WebApi.Hosting;

/// <summary>
/// Behind a reverse proxy (and behind the admin panel, which calls the API from its own container) the address of the caller is
/// the proxy's. <c>X-Forwarded-For</c>/<c>-Proto</c> are believed only when they come from a private network, which is where a proxy
/// of the same stack sits; from anywhere else they are ignored, so nobody on the internet can pick the address the login rate
/// limit counts. The same few lines are in the panel (<c>WebRazor</c>), which shares no project with the API.
/// </summary>
public static class ProxyHeaders
{
    private static readonly System.Net.IPNetwork[] PrivateNetworks =
    [
        System.Net.IPNetwork.Parse("127.0.0.0/8"), System.Net.IPNetwork.Parse("10.0.0.0/8"), System.Net.IPNetwork.Parse("172.16.0.0/12"),
        System.Net.IPNetwork.Parse("192.168.0.0/16"), System.Net.IPNetwork.Parse("::1/128"), System.Net.IPNetwork.Parse("fc00::/7")
    ];

    public static IServiceCollection AddProxyHeaders(this IServiceCollection services) =>
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            foreach (var network in PrivateNetworks)
                options.KnownIPNetworks.Add(network);
        });
}
