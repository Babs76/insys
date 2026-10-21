using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Party.Domain.Parties;
using Insys.Party.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Party.Infrastructure.Persistence;

/// <summary>The Party module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class PartyDbContext(DbContextOptions<PartyDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "party";

    public override string Schema => SchemaName;

    public DbSet<Domain.Parties.Party> Parties => Set<Domain.Parties.Party>();

    public DbSet<ConsentRecord> Consents => Set<ConsentRecord>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        PartyStaticData.Configure(modelBuilder);

        modelBuilder.Entity<Domain.Parties.Party>(party =>
        {
            party.ToTable("party");
            party.HasKey(p => new { p.TenantId, p.Id });
            party.HasAuditColumns();
            party.Ignore(p => p.DomainEvents);
            party.Ignore(p => p.HasChanges);
            party.Property(p => p.Kind).HasColumnName("party_kind").HasConversion(new UpperCaseEnumConverter<PartyKind>()).HasMaxLength(12);
            party.Property(p => p.DisplayName).HasMaxLength(200);
            party.Property(p => p.Status).HasColumnName("status_code").HasConversion(new UpperCaseEnumConverter<PartyStatus>()).HasMaxLength(12);
            party.HasOne(p => p.Individual).WithOne().HasForeignKey<Individual>(i => new { i.TenantId, i.PartyId });
            party.HasOne(p => p.Organisation).WithOne().HasForeignKey<Organisation>(o => new { o.TenantId, o.PartyId });
            Children(party, p => p.Addresses, nameof(Domain.Parties.Party.Addresses));
            Children(party, p => p.ContactPoints, nameof(Domain.Parties.Party.ContactPoints));
            Children(party, p => p.Identifiers, nameof(Domain.Parties.Party.Identifiers));
            Children(party, p => p.Roles, nameof(Domain.Parties.Party.Roles));
            party.HasMany(p => p.Relationships).WithOne().HasForeignKey(r => new { r.TenantId, r.FromPartyId }).OnDelete(DeleteBehavior.Cascade);
            party.Navigation(p => p.Relationships).UsePropertyAccessMode(PropertyAccessMode.Field);
            party.HasMany(p => p.Merges).WithOne().HasForeignKey(m => new { m.TenantId, m.MergedId });
            party.Navigation(p => p.Merges).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<Individual>(individual =>
        {
            individual.ToTable("individual");
            individual.HasKey(i => new { i.TenantId, i.PartyId });
            individual.Ignore(i => i.Id);
            individual.HasAuditColumns();
            individual.Property(i => i.TitleCode).HasMaxLength(10);
            individual.Property(i => i.FirstNames).HasMaxLength(100);
            individual.Property(i => i.Surname).HasMaxLength(100);
            individual.Property(i => i.GenderCode).HasMaxLength(10);
            individual.Property(i => i.MaritalStatusCode).HasMaxLength(15);
            individual.Property(i => i.Nationality).HasColumnType("char(2)");
            individual.Property(i => i.PreferredLanguage).HasColumnType("char(2)");
        });

        modelBuilder.Entity<Organisation>(organisation =>
        {
            organisation.ToTable("organisation");
            organisation.HasKey(o => new { o.TenantId, o.PartyId });
            organisation.Ignore(o => o.Id);
            organisation.HasAuditColumns();
            organisation.Property(o => o.LegalName).HasMaxLength(150);
            organisation.Property(o => o.TradingName).HasMaxLength(150);
            organisation.Property(o => o.LegalFormCode).HasMaxLength(10);
            organisation.Property(o => o.RegistrationNumber).HasMaxLength(40);
            organisation.Property(o => o.IndustryCode).HasMaxLength(6);
        });

        modelBuilder.Entity<Address>(address =>
        {
            address.ToTable("address");
            address.HasKey(a => new { a.TenantId, a.Id });
            address.HasAuditColumns();
            address.Property(a => a.Country).HasColumnType("char(2)");
        });

        modelBuilder.Entity<ContactPoint>(contact =>
        {
            contact.ToTable("contact_point");
            contact.HasKey(c => new { c.TenantId, c.Id });
            contact.HasAuditColumns();
        });

        modelBuilder.Entity<PartyIdentifier>(identifier =>
        {
            identifier.ToTable("party_identifier");
            identifier.HasKey(i => new { i.TenantId, i.Id });
            identifier.HasAuditColumns();
            identifier.Property(i => i.Country).HasColumnType("char(2)");
            identifier.Property(i => i.NumberHash).HasColumnType("char(64)");
        });

        modelBuilder.Entity<PartyRelationship>(relationship =>
        {
            relationship.ToTable("party_relationship");
            relationship.HasKey(r => new { r.TenantId, r.Id });
            relationship.HasAuditColumns();
        });

        modelBuilder.Entity<PartyRole>(role =>
        {
            role.ToTable("party_role");
            role.HasKey(r => new { r.TenantId, r.Id });
            role.HasAuditColumns();
        });

        modelBuilder.Entity<MergeHistory>(merge =>
        {
            merge.ToTable("merge_history");
            merge.HasKey(m => new { m.TenantId, m.Id });
            merge.HasAuditColumns(updatable: false);
            merge.Property(m => m.Snapshot).HasColumnType("jsonb");
        });

        modelBuilder.Entity<ConsentRecord>(consent =>
        {
            consent.ToTable("consent_record");
            consent.HasKey(c => new { c.TenantId, c.Id });
            consent.HasAuditColumns();
            consent.Ignore(c => c.DomainEvents);
            consent.Ignore(c => c.HasChanges);
        });
    }

    private static void Children<TChild>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Domain.Parties.Party> party,
        System.Linq.Expressions.Expression<Func<Domain.Parties.Party, IEnumerable<TChild>?>> navigation, string name)
        where TChild : class
    {
        party.HasMany(navigation).WithOne().HasForeignKey("TenantId", "PartyId").OnDelete(DeleteBehavior.Cascade);
        party.Navigation(name).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
