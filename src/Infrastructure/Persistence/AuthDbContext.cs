using Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence;

public class AuthDbContext : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>
{
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public AuthDbContext(DbContextOptions<AuthDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasDefaultSchema("auth");

        builder.Entity<ApplicationUser>().ToTable("users", "auth");
        builder.Entity<ApplicationRole>().ToTable("roles", "auth");
        builder.Entity<IdentityUserRole<Guid>>().ToTable("user_roles", "auth");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims", "auth");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins", "auth");
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable("role_claims", "auth");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens", "auth");
        builder.Entity<RefreshToken>().ToTable("refresh_tokens", "auth");
    }
}

public class ApplicationUser : IdentityUser<Guid>
{
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// True for a seeded account and for one an admin created or reset: the password is known to someone else, so the user
    /// has to replace it before anything else. Existing accounts get true from the migration too.
    /// </summary>
    public bool MustChangePassword { get; set; } = true;

    /// <summary>The user may sign in with a code mailed to their (confirmed) address instead of the authenticator app.</summary>
    public bool EmailCodesEnabled { get; set; }
}

public class ApplicationRole : IdentityRole<Guid>
{
}
