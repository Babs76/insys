using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Identity.Domain.Access;
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

    public DbSet<PrivilegedAccessGrant> Grants => Set<PrivilegedAccessGrant>();

    public DbSet<AccessReview> Reviews => Set<AccessReview>();

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
            assignment.Property(a => a.Status).HasConversion(new UpperCaseEnumConverter<AssignmentStatus>()).HasMaxLength(20);
        });

        modelBuilder.Entity<PrivilegedAccessGrant>(grant =>
        {
            grant.ToTable("privileged_access_grant");
            grant.HasKey(g => new { g.TenantId, g.Id });
            grant.HasAuditColumns();
            grant.Ignore(g => g.DomainEvents);
            grant.Ignore(g => g.HasChanges);
            grant.Property(g => g.Status).HasConversion(new UpperCaseEnumConverter<GrantStatus>()).HasMaxLength(12);
        });

        modelBuilder.Entity<AccessReview>(review =>
        {
            review.ToTable("access_review");
            review.HasKey(r => new { r.TenantId, r.Id });
            review.HasAuditColumns();
            review.Ignore(r => r.DomainEvents);
            review.Ignore(r => r.HasChanges);
            review.Property(r => r.Scope).HasConversion(new UpperCaseEnumConverter<ReviewScope>()).HasMaxLength(20);
            review.Property(r => r.Status).HasConversion(new UpperCaseEnumConverter<ReviewStatus>()).HasMaxLength(12);
            review.HasMany(r => r.Items).WithOne().HasForeignKey(i => new { i.TenantId, i.ReviewId });
            review.Navigation(r => r.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<ReviewItem>(item =>
        {
            item.ToTable("access_review_item");
            item.HasKey(i => new { i.TenantId, i.Id });
            item.HasAuditColumns();
            item.Property(i => i.RoleCode).HasMaxLength(30);
            item.Property(i => i.Decision).HasConversion(new UpperCaseEnumConverter<ItemDecision>()).HasMaxLength(12);
        });

        modelBuilder.Entity<Session>(session =>
        {
            session.ToTable("session");
            session.HasKey(s => new { s.TenantId, s.Id });
            session.HasAuditColumns();
        });
    }
}
