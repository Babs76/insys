/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Communications
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
using System.Text.Json;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.BuildingBlocks.Infrastructure.ProviderCallbacks;
using Insys.Communications.Domain;
using Insys.Communications.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Insys.Communications.Infrastructure.Persistence;

/// <summary>The Communications module's context: the communication schema only, on the insys_communication role (DBA-004).</summary>
public sealed class CommunicationsDbContext(DbContextOptions<CommunicationsDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "communication";

    public override string Schema => SchemaName;

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();

    public DbSet<NotificationPreference> Preferences => Set<NotificationPreference>();

    public DbSet<DeviceRegistration> Devices => Set<DeviceRegistration>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        CommunicationsStaticData.Configure(modelBuilder);
        CallbackEndpointModel.Configure(modelBuilder, providerCodeLength: 20);

        modelBuilder.Entity<Notification>(notification =>
        {
            notification.ToTable("notification");
            notification.HasKey(n => new { n.TenantId, n.Id });
            notification.HasAuditColumns();
            notification.Ignore(n => n.DomainEvents);
            notification.Ignore(n => n.HasChanges);
            notification.Ignore(n => n.IsMarketing);
            notification.Property(n => n.RequestKey).HasMaxLength(200);
            notification.Property(n => n.TypeCode).HasMaxLength(30);
            notification.Property(n => n.Category).HasMaxLength(10);
            notification.Property(n => n.ConsentPurpose).HasMaxLength(20);
            notification.Property(n => n.SourceType).HasMaxLength(30);
            notification.Property(n => n.ProductCode).HasMaxLength(15);
            notification.Property(n => n.OrgNodeCode).HasMaxLength(30);
            notification.Property(n => n.MergeValues).HasColumnType("jsonb").HasConversion(
                v => JsonSerializer.Serialize(v, JsonSerializerOptions.Web),
                v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, JsonSerializerOptions.Web) ?? new Dictionary<string, string>(),
                new ValueComparer<IReadOnlyDictionary<string, string>>((a, b) => a!.Count == b!.Count && !a.Except(b).Any(),
                    v => v.Aggregate(0, (h, x) => HashCode.Combine(h, x.Key, x.Value)), v => new Dictionary<string, string>(v)));
            notification.Property(n => n.Channels).HasColumnType("text[]").HasConversion(Strings(), StringsComparer());
            notification.Property(n => n.Language).HasMaxLength(2);
            notification.Property(n => n.Subject).HasMaxLength(200);
            notification.Property(n => n.Status).HasConversion(new UpperCaseEnumConverter<NotificationStatus>()).HasMaxLength(25);
            notification.Property(n => n.Channel).HasMaxLength(10);
            notification.HasMany(n => n.DeliveryAttempts).WithOne().HasForeignKey(a => new { a.TenantId, a.NotificationId })
                .HasPrincipalKey(n => new { n.TenantId, n.Id });
            notification.Navigation(n => n.DeliveryAttempts).HasField("_attempts").UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<DeliveryAttempt>(attempt =>
        {
            attempt.ToTable("delivery_attempt");
            attempt.HasKey(a => new { a.TenantId, a.Id });
            attempt.Property(a => a.Channel).HasMaxLength(10);
            attempt.Property(a => a.ProviderCode).HasMaxLength(20);
            attempt.Property(a => a.ProviderRef).HasMaxLength(120);
            attempt.Property(a => a.MaskedAddress).HasMaxLength(120);
            attempt.Property(a => a.Status).HasConversion(new UpperCaseEnumConverter<AttemptStatus>()).HasMaxLength(15);
            attempt.Property(a => a.FailureCode).HasMaxLength(40);
        });

        modelBuilder.Entity<NotificationPreference>(preference =>
        {
            preference.ToTable("notification_preference");
            preference.HasKey(p => new { p.TenantId, p.Id });
            preference.HasAuditColumns();
            preference.Ignore(p => p.DomainEvents);
            preference.Ignore(p => p.HasChanges);
            preference.Property(p => p.TypeCode).HasMaxLength(30);
            preference.Property(p => p.Channels).HasColumnType("text[]").HasConversion(Strings(), StringsComparer());
        });

        modelBuilder.Entity<DeviceRegistration>(device =>
        {
            device.ToTable("device_registration");
            device.HasKey(d => new { d.TenantId, d.Id });
            device.HasAuditColumns();
            device.Ignore(d => d.DomainEvents);
            device.Ignore(d => d.HasChanges);
            device.Property(d => d.Platform).HasMaxLength(10);
            device.Property(d => d.TokenHash).HasMaxLength(64).IsFixedLength();
        });
    }

    private static Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<IReadOnlyList<string>, string[]> Strings() =>
        new(v => v.ToArray(), v => v.ToList());

    private static ValueComparer<IReadOnlyList<string>> StringsComparer() =>
        new((a, b) => a!.SequenceEqual(b!), v => v.Aggregate(0, (h, x) => HashCode.Combine(h, x)), v => v.ToList());
}
