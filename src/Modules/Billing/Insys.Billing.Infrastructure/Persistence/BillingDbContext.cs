using Insys.Billing.Domain.Billing;
using Insys.Billing.Infrastructure.StaticData;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Insys.Billing.Infrastructure.Persistence;

/// <summary>The Billing module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class BillingDbContext(DbContextOptions<BillingDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "billing";

    public override string Schema => SchemaName;

    public DbSet<BillingAccount> Accounts => Set<BillingAccount>();

    public DbSet<Invoice> Invoices => Set<Invoice>();

    public DbSet<RefundRequest> Refunds => Set<RefundRequest>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        BillingStaticData.Configure(modelBuilder);

        modelBuilder.Entity<BillingAccount>(account =>
        {
            account.ToTable("billing_account");
            account.HasKey(a => new { a.TenantId, a.Id });
            account.HasAuditColumns();
            account.Ignore(a => a.DomainEvents);
            account.Ignore(a => a.HasChanges);
            account.Property(a => a.AccountNumber).HasMaxLength(15);
            account.Property(a => a.Currency).HasColumnType("char(3)");
            account.Property(a => a.CollectionMethod).HasMaxLength(15);
            account.Property(a => a.MandateRef).HasMaxLength(60);
            account.Property(a => a.Status).HasConversion(new UpperCaseEnumConverter<AccountStatus>()).HasMaxLength(12);
            account.HasMany(a => a.Schedules).WithOne().HasForeignKey(s => new { s.TenantId, s.BillingAccountId });
            account.Navigation(a => a.Schedules).UsePropertyAccessMode(PropertyAccessMode.Field);
            account.HasMany(a => a.Arrears).WithOne().HasForeignKey(r => new { r.TenantId, r.BillingAccountId });
            account.Navigation(a => a.Arrears).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<PremiumSchedule>(schedule =>
        {
            schedule.ToTable("premium_schedule");
            schedule.HasKey(s => new { s.TenantId, s.Id });
            schedule.HasAuditColumns();
            schedule.Property(s => s.SourceDomain).HasMaxLength(10);
            schedule.Property(s => s.Frequency).HasMaxLength(10);
            schedule.Property(s => s.InstalmentAmount).HasPrecision(18, 2);
            schedule.Property(s => s.Currency).HasColumnType("char(3)");
            schedule.Property(s => s.ProductVersionRef).HasMaxLength(30);
            schedule.HasMany(s => s.Instalments).WithOne().HasForeignKey(i => new { i.TenantId, i.ScheduleId });
            schedule.Navigation(s => s.Instalments).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<Instalment>(instalment =>
        {
            instalment.ToTable("premium_instalment");
            instalment.HasKey(i => new { i.TenantId, i.Id });
            instalment.HasAuditColumns();
            instalment.Property(i => i.Amount).HasPrecision(18, 2);
            instalment.Property(i => i.Status).HasConversion(new UpperCaseEnumConverter<InstalmentStatus>()).HasMaxLength(12);
        });

        modelBuilder.Entity<ArrearsCase>(arrears =>
        {
            arrears.ToTable("arrears_case");
            arrears.HasKey(a => new { a.TenantId, a.Id });
            arrears.HasAuditColumns();
            arrears.Property(a => a.BucketCode).HasMaxLength(10);
            arrears.Property(a => a.AmountOverdue).HasPrecision(18, 2);
            arrears.Property(a => a.Currency).HasColumnType("char(3)");
            arrears.Property(a => a.NextAction).HasMaxLength(15);
            arrears.Property(a => a.Status).HasMaxLength(10);
        });

        modelBuilder.Entity<Invoice>(invoice =>
        {
            invoice.ToTable("invoice");
            invoice.HasKey(i => new { i.TenantId, i.Id });
            invoice.HasAuditColumns();
            invoice.Ignore(i => i.DomainEvents);
            invoice.Ignore(i => i.HasChanges);
            invoice.Ignore(i => i.Outstanding);
            invoice.Ignore(i => i.Credited);
            invoice.Property(i => i.InvoiceNumber).HasMaxLength(20);
            invoice.Property(i => i.DocumentType).HasConversion(new UpperCaseEnumConverter<DocumentType>()).HasMaxLength(12);
            invoice.Property(i => i.SourceDomain).HasMaxLength(10);
            invoice.Property(i => i.ProductVersionRef).HasMaxLength(30);
            invoice.Property(i => i.Currency).HasColumnType("char(3)");
            invoice.Property(i => i.NetAmount).HasPrecision(18, 2);
            invoice.Property(i => i.TaxAmount).HasPrecision(18, 2);
            invoice.Property(i => i.TotalAmount).HasPrecision(18, 2);
            invoice.Property(i => i.AllocatedAmount).HasPrecision(18, 2);
            invoice.Property(i => i.Status).HasConversion(new UpperCaseEnumConverter<InvoiceStatus>()).HasMaxLength(12);
            invoice.HasMany(i => i.Lines).WithOne().HasForeignKey(l => new { l.TenantId, l.InvoiceId });
            invoice.Navigation(i => i.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
            invoice.HasMany(i => i.Adjustments).WithOne().HasForeignKey(a => new { a.TenantId, a.InvoiceId });
            invoice.Navigation(i => i.Adjustments).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<InvoiceLine>(line =>
        {
            line.ToTable("invoice_line");
            line.HasKey(l => new { l.TenantId, l.Id });
            line.HasAuditColumns();
            line.Property(l => l.ChargeType).HasMaxLength(20);
            line.Property(l => l.Description).HasMaxLength(200);
            line.Property(l => l.Amount).HasPrecision(18, 2);
            line.Property(l => l.TaxCode).HasMaxLength(15);
        });

        modelBuilder.Entity<Adjustment>(adjustment =>
        {
            adjustment.ToTable("adjustment");
            adjustment.HasKey(a => new { a.TenantId, a.Id });
            adjustment.HasAuditColumns(updatable: false);
            adjustment.Property(a => a.ReasonCode).HasMaxLength(15);
            adjustment.Property(a => a.Amount).HasPrecision(18, 2);
            adjustment.Property(a => a.Currency).HasColumnType("char(3)");

            // So the credit note is inserted before the adjustment that names it (fk_adjustment_credit_note_id).
            adjustment.HasOne<Invoice>().WithMany().HasForeignKey(a => new { a.TenantId, a.CreditNoteId });
        });

        modelBuilder.Entity<RefundRequest>(refund =>
        {
            refund.ToTable("refund_request");
            refund.HasKey(r => new { r.TenantId, r.Id });
            refund.HasAuditColumns();
            refund.Ignore(r => r.DomainEvents);
            refund.Ignore(r => r.HasChanges);
            refund.Property(r => r.Amount).HasPrecision(18, 2);
            refund.Property(r => r.Currency).HasColumnType("char(3)");
            refund.Property(r => r.ReasonCode).HasMaxLength(15);
            refund.Property(r => r.PayeeBankHash).HasMaxLength(64);
            refund.Ignore(r => r.SubjectHash);
            refund.Property(r => r.Status).HasConversion(new UpperCaseEnumConverter<RefundStatus>()).HasMaxLength(12);
        });
    }
}
