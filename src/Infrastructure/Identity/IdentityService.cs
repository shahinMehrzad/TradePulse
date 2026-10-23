using Application.Interfaces;
using Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;

namespace Infrastructure.Identity;

public sealed class IdentityService(UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager) : IIdentityService
{
    public async Task<AuthenticatedUser?> CheckCredentialsAsync(string email, string username, string password, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByEmailAsync(email) ?? await userManager.FindByNameAsync(username);
        if (user is null)
            return null;

        var result = await signInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        return result.Succeeded ? await ToAuthenticatedUserAsync(user) : null;
    }

    public async Task<AuthenticatedUser?> FindByIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        return user is null ? null : await ToAuthenticatedUserAsync(user);
    }

    private async Task<AuthenticatedUser> ToAuthenticatedUserAsync(ApplicationUser user) =>
        new(user.Id, user.UserName ?? string.Empty, user.Email ?? string.Empty, [.. await userManager.GetRolesAsync(user)]);
}
