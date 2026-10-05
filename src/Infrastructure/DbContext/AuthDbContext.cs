using Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.DbContext;

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
}

public class ApplicationRole : IdentityRole<Guid>
{
}
