namespace Application.Interfaces;

public sealed record AuthenticatedUser(Guid Id, string UserName, string Email, IReadOnlyList<string> Roles);

public interface IIdentityService
{    
    Task<AuthenticatedUser?> CheckCredentialsAsync(string email, string username, string password, CancellationToken cancellationToken);
    Task<AuthenticatedUser?> FindByIdAsync(Guid userId, CancellationToken cancellationToken);
}