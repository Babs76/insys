using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Payments.Domain.Payments;
using Insys.Payments.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Payments.Infrastructure.Persistence;

/// <summary>The Payments module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "payment";

    public override string Schema => SchemaName;

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<PayoutBatch> Batches => Set<PayoutBatch>();

    public DbSet<ReconciliationException> Exceptions => Set<ReconciliationException>();

    public DbSet<BankStatementLine> StatementLines => Set<BankStatementLine>();

    public DbSet<ProviderCallback> Callbacks => Set<ProviderCallback>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        PaymentsStaticData.Configure(modelBuilder);

        modelBuilder.Entity<Payment>(payment =>
        {
            payment.ToTable("payment");
            payment.HasKey(p => new { p.TenantId, p.Id });
            payment.HasAuditColumns();
            payment.Ignore(p => p.DomainEvents);
            payment.Ignore(p => p.HasChanges);
            payment.Ignore(p => p.Allocated);
            payment.Ignore(p => p.IsFinal);
            payment.Property(p => p.PaymentRef).HasMaxLength(25);
            payment.Property(p => p.Direction).HasConversion(new UpperCaseEnumConverter<Direction>()).HasMaxLength(8);
            payment.Property(p => p.Purpose).HasMaxLength(15);
            payment.Property(p => p.MethodCode).HasMaxLength(15);
            payment.Property(p => p.ProviderCode).HasMaxLength(15);
            payment.Property(p => p.Amount).HasPrecision(18, 2);
            payment.Property(p => p.Currency).HasColumnType("char(3)");
            payment.Property(p => p.Status).HasConversion(new UpperCaseEnumConverter<PaymentStatus>()).HasMaxLength(12);
            payment.Property(p => p.ExternalRef).HasMaxLength(80);
            payment.Property(p => p.IdempotencyKey).HasMaxLength(80);
            payment.Property(p => p.BusinessRefType).HasMaxLength(30);
            // So a batch is inserted before the payments assigned to it (fk_payment_payout_batch_id).
            payment.HasOne<PayoutBatch>().WithMany().HasForeignKey(p => new { p.TenantId, p.PayoutBatchId });
            payment.HasMany(p => p.History).WithOne().HasForeignKey(h => new { h.TenantId, h.PaymentId });
            payment.Navigation(p => p.History).UsePropertyAccessMode(PropertyAccessMode.Field);
            payment.HasMany(p => p.Allocations).WithOne().HasForeignKey(a => new { a.TenantId, a.PaymentId });
            payment.Navigation(p => p.Allocations).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        // A history: inserted only, never updated or deleted (PAY-002; V0014 refuses both).
        modelBuilder.Entity<StatusChange>(change =>
        {
            change.ToTable("payment_status_history");
            change.HasKey(c => new { c.TenantId, c.Id });
            change.HasAuditColumns(updatable: false);
            change.Property(c => c.FromStatus).HasConversion(new UpperCaseEnumConverter<PaymentStatus>()).HasMaxLength(12);
            change.Property(c => c.ToStatus).HasConversion(new UpperCaseEnumConverter<PaymentStatus>()).HasMaxLength(12);
            change.Property(c => c.Source).HasMaxLength(15);
        });

        modelBuilder.Entity<PaymentAllocation>(allocation =>
        {
            allocation.ToTable("payment_allocation");
            allocation.HasKey(a => new { a.TenantId, a.Id });
            allocation.HasAuditColumns();
            allocation.Property(a => a.Amount).HasPrecision(18, 2);
            allocation.Property(a => a.Currency).HasColumnType("char(3)");
            allocation.Property(a => a.AllocationRule).HasMaxLength(15);
            allocation.HasOne<PaymentAllocation>().WithMany().HasForeignKey(a => new { a.TenantId, a.ReversedById });
        });

        modelBuilder.Entity<PayoutBatch>(batch =>
        {
            batch.ToTable("payout_batch");
            batch.HasKey(b => new { b.TenantId, b.Id });
            batch.HasAuditColumns();
            batch.Ignore(b => b.DomainEvents);
            batch.Ignore(b => b.HasChanges);
            batch.Property(b => b.BatchRef).HasMaxLength(20);
            batch.Property(b => b.Purpose).HasMaxLength(15);
            batch.Property(b => b.TotalAmount).HasPrecision(18, 2);
            batch.Property(b => b.Currency).HasColumnType("char(3)");
            batch.Property(b => b.Status).HasConversion(new UpperCaseEnumConverter<BatchStatus>()).HasMaxLength(12);
        });

        modelBuilder.Entity<ReconciliationException>(exception =>
        {
            exception.ToTable("reconciliation_exception");
            exception.HasKey(e => new { e.TenantId, e.Id });
            exception.HasAuditColumns();
            exception.Ignore(e => e.DomainEvents);
            exception.Ignore(e => e.HasChanges);
            exception.Property(e => e.ExceptionType).HasMaxLength(15);
            exception.Property(e => e.Amount).HasPrecision(18, 2);
            exception.Property(e => e.Status).HasConversion(new UpperCaseEnumConverter<ExceptionStatus>()).HasMaxLength(30);
        });

        modelBuilder.Entity<BankStatementLine>(line =>
        {
            line.ToTable("bank_statement_line");
            line.HasKey(l => new { l.TenantId, l.Id });
            line.HasAuditColumns();
            line.Ignore(l => l.DomainEvents);
            line.Ignore(l => l.HasChanges);
            line.Property(l => l.AccountRef).HasMaxLength(15);
            line.Property(l => l.Amount).HasPrecision(18, 2);
            line.Property(l => l.Currency).HasColumnType("char(3)");
            line.Property(l => l.Reference).HasMaxLength(80);
            line.Property(l => l.MatchStatus).HasMaxLength(12);
        });

        modelBuilder.Entity<ProviderCallback>(callback =>
        {
            callback.ToTable("provider_callback");
            callback.HasKey(c => new { c.TenantId, c.Id });
            callback.HasAuditColumns();
            callback.Property(c => c.ProviderCode).HasMaxLength(15);
            callback.Property(c => c.ExternalRef).HasMaxLength(80);
            callback.Property(c => c.Payload).HasColumnType("jsonb");
        });
    }
}

/// <summary>A provider callback as received (payment.provider_callback): kept whatever its signature, for investigation.</summary>
public sealed class ProviderCallback : BuildingBlocks.Domain.Entity<Guid>, BuildingBlocks.Domain.ITenantScoped
{
    public Guid TenantId { get; set; }

    public string ProviderCode { get; set; } = "";

    public string ExternalRef { get; set; } = "";

    public string Payload { get; set; } = "{}";

    public bool SignatureValid { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public long RowVersion { get; set; }

    public static ProviderCallback Create(string provider, string externalRef, string payload, bool valid, DateTimeOffset now, bool processed) => new()
    {
        Id = Guid.CreateVersion7(), ProviderCode = provider, ExternalRef = externalRef, Payload = payload, SignatureValid = valid, ReceivedAt = now,
        ProcessedAt = processed ? now : null,
    };
}
