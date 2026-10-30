using Application.Interfaces;
using Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Identity;

public sealed class UserAdministration(UserManager<ApplicationUser> userManager, IConfiguration configuration) : IUserAdministration
{
    private string AdminRole => configuration["AdminConfigurations:Role"]
                                ?? throw new InvalidOperationException("AdminConfigurations:Role must be set.");

    public async Task<IReadOnlyList<UserSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var users = await userManager.Users.OrderBy(u => u.UserName).ToListAsync(cancellationToken);

        var summaries = new List<UserSummary>(users.Count);
        foreach (var user in users)
            summaries.Add(await ToSummaryAsync(user));
        return summaries;
    }

    public async Task<CreatedUser> CreateAdministratorAsync(string userName, string email, CancellationToken cancellationToken)
    {
        var password = TemporaryPassword.Generate();
        var user = new ApplicationUser { UserName = userName, Email = email, MustChangePassword = true };

        var created = await userManager.CreateAsync(user, password);
        if (!created.Succeeded)
            return new CreatedUser(null, null, [.. created.Errors.Select(e => e.Description)]);

        var inRole = await userManager.AddToRoleAsync(user, AdminRole);
        if (!inRole.Succeeded)
        {
            // Never leave an account that exists but belongs to nobody.
            await userManager.DeleteAsync(user);
            return new CreatedUser(null, null, [.. inRole.Errors.Select(e => e.Description)]);
        }

        return new CreatedUser(await ToSummaryAsync(user), password, []);
    }

    public async Task<(UserChangeOutcome Outcome, string? TemporaryPassword)> ResetPasswordAsync(Guid userId, Guid actingUserId, CancellationToken cancellationToken)
    {
        if (userId == actingUserId)
            return (UserChangeOutcome.Self, null);
        if (await userManager.FindByIdAsync(userId.ToString()) is not { } user)
            return (UserChangeOutcome.NotFound, null);

        var password = TemporaryPassword.Generate();
        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var reset = await userManager.ResetPasswordAsync(user, token, password);
        if (!reset.Succeeded)
            throw new InvalidOperationException("Could not reset the password: " + string.Join(" ", reset.Errors.Select(e => e.Description)));

        user.MustChangePassword = true;
        await userManager.UpdateAsync(user);
        await userManager.ResetAccessFailedCountAsync(user);
        return (UserChangeOutcome.Done, password);
    }

    public async Task<UserChangeOutcome> ResetTwoFactorAsync(Guid userId, Guid actingUserId, CancellationToken cancellationToken)
    {
        if (userId == actingUserId)
            return UserChangeOutcome.Self;
        if (await userManager.FindByIdAsync(userId.ToString()) is not { } user)
            return UserChangeOutcome.NotFound;

        await userManager.SetTwoFactorEnabledAsync(user, false);
        await userManager.ResetAuthenticatorKeyAsync(user);
        await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 0); // forgets the old codes
        await userManager.ResetAccessFailedCountAsync(user);
        return UserChangeOutcome.Done;
    }

    public async Task<UserChangeOutcome> SetDisabledAsync(Guid userId, bool disabled, Guid actingUserId, CancellationToken cancellationToken)
    {
        if (userId == actingUserId)
            return UserChangeOutcome.Self;
        if (await userManager.FindByIdAsync(userId.ToString()) is not { } user)
            return UserChangeOutcome.NotFound;

        if (disabled && await userManager.IsInRoleAsync(user, AdminRole) && !await AnotherActiveAdministratorExistsAsync(user))
            return UserChangeOutcome.LastAdministrator;

        await userManager.SetLockoutEnabledAsync(user, true);
        await userManager.SetLockoutEndDateAsync(user, disabled ? ApplicationUserExtensions.DisabledUntil : null);
        await userManager.ResetAccessFailedCountAsync(user);
        return UserChangeOutcome.Done;
    }

    private async Task<bool> AnotherActiveAdministratorExistsAsync(ApplicationUser except) =>
        (await userManager.GetUsersInRoleAsync(AdminRole)).Any(u => u.Id != except.Id && !u.IsDisabled());

    private async Task<UserSummary> ToSummaryAsync(ApplicationUser user) => new(
        user.Id, user.UserName ?? string.Empty, user.Email ?? string.Empty, [.. await userManager.GetRolesAsync(user)],
        user.TwoFactorEnabled, user.MustChangePassword, user.IsDisabled());
}
