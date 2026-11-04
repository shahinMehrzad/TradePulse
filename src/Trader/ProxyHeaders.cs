using Microsoft.AspNetCore.HttpOverrides;

namespace Trader;

/// <summary>
/// The panel calls the vault from its own container and tells it who the visitor is in <c>X-Forwarded-For</c>, so that an alert about a wrong code
/// can say where it came from. The header is believed only from a private network (the stack's own), never from anywhere else. The same few lines
/// are in the API and the panel, which share no project with the vault.
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
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
            options.ForwardLimit = 1;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            foreach (var network in PrivateNetworks)
                options.KnownIPNetworks.Add(network);
        });
}
