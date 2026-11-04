using Application.Interfaces;

namespace Infrastructure.Identity;

/// <summary>The address of work that no visitor started (the server's own command line): one address for all of it.</summary>
public sealed class LocalClientAddress : IClientAddress
{
    public string Address => "local";
}
