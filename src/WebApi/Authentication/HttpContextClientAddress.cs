using Application.Interfaces;

namespace WebApi.Authentication;

/// <summary>
/// The address of the visitor whose request is being handled. Behind a trusted proxy the forwarded-headers middleware has already put the
/// visitor's address there, and from anyone else the header is ignored (see <c>ProxyHeaders</c>), so nobody can choose the address a wrong
/// password is held against. An IPv4 address that arrives in its IPv6 form is written as IPv4, so one visitor is one address.
/// </summary>
public sealed class HttpContextClientAddress(IHttpContextAccessor accessor) : IClientAddress
{
    public string Address
    {
        get
        {
            var address = accessor.HttpContext?.Connection.RemoteIpAddress;
            if (address is null)
                return "unknown";

            return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
        }
    }
}
