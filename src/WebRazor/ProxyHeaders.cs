using Microsoft.AspNetCore.HttpOverrides;

namespace WebRazor;

/// <summary>
/// Behind the reverse proxy the address and the scheme of the caller are the proxy's. <c>X-Forwarded-For</c>/<c>-Proto</c> are
/// believed only when they come from a private network, which is where the proxy of the same stack sits; from anywhere else
/// they are ignored. The same few lines are in the API, which shares no project with the panel.
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
