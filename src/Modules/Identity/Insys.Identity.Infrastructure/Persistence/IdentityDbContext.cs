/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Identity
 * CONFIDENTIALITY AND INTELLECTUAL PROPERTY NOTICE
 * This source code and its associated documentation contain confidential and
 * proprietary information belonging to OpenFactor Technology Group or its
 * applicable intellectual property rights holder.
 * This material is provided solely for authorised development, maintenance,
 * operation or evaluation purposes, as applicable. No person may reproduce,
 * modify, distribute, disclose, publish or otherwise use this material except
 * as expressly permitted by an applicable written agreement or licence.
 * No ownership rights or licence to any intellectual property are granted
 * by the inclusion of this notice, except as expressly provided in an
 * applicable written agreement or licence.
 * Third-party software, open-source components and customer-owned materials
 * remain subject to their respective ownership rights and applicable licences
 * or agreements.
 * UNAUTHORISED ACCESS, USE, COPYING OR DISCLOSURE IS PROHIBITED.
 * ============================================================================
 */
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Identity.Domain.Access;
using Insys.Identity.Domain.Tenants;
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

    public DbSet<TenantAdministratorRequest> TenantAdministratorRequests => Set<TenantAdministratorRequest>();

    /// <summary>A new tenant's first administrator is written as that tenant, once a second platform administrator approves (OQ-067).</summary>
    protected override bool AllowsOperatorActingAsTenant => true;

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
            user.Property(u => u.PreferredLanguage).HasMaxLength(2);
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

        modelBuilder.Entity<TenantAdministratorRequest>(request =>
        {
            request.ToTable("tenant_administrator_request");
            request.HasKey(r => new { r.TenantId, r.Id });
            request.HasAuditColumns();
            request.Ignore(r => r.DomainEvents);
            request.Ignore(r => r.HasChanges);
            request.Property(r => r.Username).HasMaxLength(120);
            request.Property(r => r.DisplayName).HasMaxLength(120);
            request.Property(r => r.IdpCode).HasMaxLength(15);
            request.Property(r => r.ExternalSubject).HasMaxLength(120);
            request.Property(r => r.Role).HasColumnName("role_code").HasMaxLength(30);
            request.Property(r => r.Status).HasConversion(new UpperCaseEnumConverter<TenantAdministratorRequestStatus>()).HasMaxLength(20);
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
