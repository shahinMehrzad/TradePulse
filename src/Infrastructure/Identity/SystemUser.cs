using Application.Interfaces;

namespace Infrastructure.Identity;

/// <summary>The current user of work that has no signed-in user (the collector). The API replaces it with the user of the request.</summary>
public sealed class SystemUser : ICurrentUser
{
    public string Name => "system";
}
