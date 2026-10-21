using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Identity.Domain.Users;
using Insys.Identity.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Identity.Infrastructure.Persistence;

/// <summary>The Identity module's context: the iam schema only, on the insys_iam role.</summary>
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "iam";

    public override string Schema => SchemaName;

    public DbSet<UserAccount> Users => Set<UserAccount>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        IdentityStaticData.Configure(modelBuilder);

        modelBuilder.Entity<UserAccount>(user =>
        {
            user.ToTable("user_account");
            user.HasKey(u => new { u.TenantId, u.Id });
            user.HasAuditColumns();
            user.Ignore(u => u.DomainEvents);
            user.Ignore(u => u.HasChanges);
            user.Property(u => u.Username).HasMaxLength(120);
            user.Property(u => u.DisplayName).HasMaxLength(120);
            user.Property(u => u.IdpCode).HasMaxLength(15);
            user.Property(u => u.ExternalSubject).HasMaxLength(120);
            user.Property(u => u.Status).HasConversion(new UpperCaseEnumConverter<UserStatus>()).HasMaxLength(12);
            user.HasMany(u => u.RoleAssignments).WithOne().HasForeignKey(a => new { a.TenantId, a.UserId });
            user.Navigation(u => u.RoleAssignments).UsePropertyAccessMode(PropertyAccessMode.Field);
            user.HasMany(u => u.Sessions).WithOne().HasForeignKey(s => new { s.TenantId, s.UserId });
            user.Navigation(u => u.Sessions).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<RoleAssignment>(assignment =>
        {
            assignment.ToTable("user_role_assignment");
            assignment.HasKey(a => new { a.TenantId, a.Id });
            assignment.HasAuditColumns();
            assignment.Property(a => a.RoleCode).HasMaxLength(30);
        });

        modelBuilder.Entity<Session>(session =>
        {
            session.ToTable("session");
            session.HasKey(s => new { s.TenantId, s.Id });
            session.HasAuditColumns();
        });
    }
}
