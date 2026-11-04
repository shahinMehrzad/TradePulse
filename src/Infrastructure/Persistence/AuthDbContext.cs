using Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Infrastructure.Persistence;

public class AuthDbContext : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>
{
    /// <summary>Written as "unspecified" (Npgsql refuses UTC for a <c>timestamp</c> column), read back as UTC.</summary>
    private static readonly ValueConverter<DateTime, DateTime> UtcTimestamp =
        new(v => DateTime.SpecifyKind(v, DateTimeKind.Unspecified), v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<LoginThrottle> LoginThrottles => Set<LoginThrottle>();

    public DbSet<UsedTwoFactorCode> UsedTwoFactorCodes => Set<UsedTwoFactorCode>();

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

        builder.Entity<UsedTwoFactorCode>(e =>
        {
            e.ToTable("used_two_factor_codes", "auth");
            e.HasKey(c => c.Id);
            e.Property(c => c.CodeHash).HasMaxLength(64).IsRequired();
            // The unique index is what lets only one of two uses of the same code at the same moment through.
            e.HasIndex(c => new { c.UserId, c.CodeHash }).IsUnique();
            e.Property(c => c.UsedAt).HasColumnType("timestamp").HasConversion(UtcTimestamp);
        });

        builder.Entity<LoginThrottle>(e =>
        {
            e.ToTable("login_throttles", "auth");
            e.HasKey(t => t.Id);
            e.Property(t => t.Address).HasMaxLength(64).IsRequired();
            // One row per user, kind and address: the unique index is also what makes two first wrong guesses at once safe.
            e.HasIndex(t => new { t.UserId, t.Kind, t.Address }).IsUnique();

            // Time columns are "timestamp" (no zone) and always UTC, like everywhere else: Npgsql refuses Kind=Utc for them.
            e.Property(t => t.FirstFailureAt).HasColumnType("timestamp").HasConversion(UtcTimestamp);
            e.Property(t => t.LockedUntil).HasColumnType("timestamp").HasConversion(UtcTimestamp);
            e.Property(t => t.UpdatedAt).HasColumnType("timestamp").HasConversion(UtcTimestamp);
        });
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
