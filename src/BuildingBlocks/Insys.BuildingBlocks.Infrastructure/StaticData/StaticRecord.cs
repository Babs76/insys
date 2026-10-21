using System.Text.Json;
using Insys.BuildingBlocks.Application.Security;
using Microsoft.EntityFrameworkCore;

namespace Insys.BuildingBlocks.Infrastructure.StaticData;

/// <summary>
/// The common record model every static data table carries (V0002 header; static data specification §3).
/// Generated entities add the table's own columns.
/// </summary>
public abstract class StaticRecord
{
    public Guid Id { get; set; }

    /// <summary>NULL for platform-global rows (scopes G and GT); otherwise the owning tenant.</summary>
    public Guid? TenantId { get; set; }

    public string Code { get; set; } = "";

    public int Version { get; set; }

    public DateOnly EffectiveFrom { get; set; }

    public DateOnly? EffectiveTo { get; set; }

    public string RecordStatus { get; set; } = "DRAFT";

    public Guid? SupersedesId { get; set; }

    public JsonElement? Labels { get; set; }

    public string? Description { get; set; }

    public string? RecordSource { get; set; }

    public string? RecordSourceVersion { get; set; }

    public JsonElement? ExternalMappings { get; set; }

    public string RecordClass { get; set; } = "INTERNAL";

    public int? SortOrder { get; set; }

    public JsonElement? DisplayFlags { get; set; }

    public Guid? ChangeRequestId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The maker: the user who created the draft.</summary>
    public string CreatedBy { get; set; } = "";

    public DateTimeOffset? ApprovedAt { get; set; }

    public string? ApprovedBy { get; set; }

    public string? ChangeReason { get; set; }

    public long RowVersion { get; set; }

    internal void ApplyCommon(StaticRecordInput input)
    {
        EffectiveFrom = input.EffectiveFrom ?? EffectiveFrom;
        EffectiveTo = input.EffectiveTo;
        Labels = input.Labels;
        Description = input.Description;
        RecordSource = input.RecordSource;
        RecordSourceVersion = input.RecordSourceVersion;
        ExternalMappings = input.ExternalMappings;
        RecordClass = input.RecordClass ?? RecordClass;
        SortOrder = input.SortOrder;
        DisplayFlags = input.DisplayFlags;
        ChangeReason = input.ChangeReason;
    }
}

/// <summary>A static data entity with its generated input type.</summary>
public abstract class StaticRecord<TInput> : StaticRecord
    where TInput : StaticRecordInput
{
    /// <summary>Copies the table's own fields from the input; masked fields are encrypted with the protector (DBA-014).</summary>
    public abstract void ApplyFields(TInput input, IFieldProtector protector);
}

/// <summary>The common fields a client sends when creating or editing a draft.</summary>
public abstract record StaticRecordInput
{
    private static readonly string[] RecordClasses = ["PUBLIC", "INTERNAL", "CONFIDENTIAL", "PERSONAL", "HIGHLY_SENSITIVE"];

    /// <summary>The business key. TODO(spec): composite natural keys are composed by the service, but the key fields are not identified.</summary>
    public string Code { get; init; } = "";

    public DateOnly? EffectiveFrom { get; init; }

    public DateOnly? EffectiveTo { get; init; }

    public JsonElement? Labels { get; init; }

    public string? Description { get; init; }

    public string? RecordSource { get; init; }

    public string? RecordSourceVersion { get; init; }

    public JsonElement? ExternalMappings { get; init; }

    public string? RecordClass { get; init; }

    public int? SortOrder { get; init; }

    public JsonElement? DisplayFlags { get; init; }

    /// <summary>Why the change is made; required for the change request and the audit trail (SDM-014).</summary>
    public string ChangeReason { get; init; } = "";

    /// <summary>Checks the common fields and then the table's own fields (SDM-013, on save).</summary>
    public IReadOnlyDictionary<string, string[]> Validate()
    {
        var errors = new StaticFieldErrors();
        errors.Required("code", Code);
        errors.MaxLength("code", Code, 120);
        errors.Required("changeReason", ChangeReason);
        errors.MaxLength("recordSource", RecordSource, 60);
        errors.MaxLength("recordSourceVersion", RecordSourceVersion, 30);
        errors.OneOf("recordClass", RecordClass, RecordClasses);
        if (EffectiveFrom is { } from && EffectiveTo is { } to && to <= from)
        {
            errors.Add("effectiveTo", "effectiveTo must be after effectiveFrom.");
        }

        ValidateFields(errors);
        return errors.ToDictionary();
    }

    protected abstract void ValidateFields(StaticFieldErrors errors);
}

/// <summary>Field errors keyed by the JSON field name.</summary>
public sealed class StaticFieldErrors
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list))
        {
            _errors[field] = list = [];
        }

        list.Add(message);
    }

    public void Required(string field, object? value)
    {
        if (value is null || (value is string s && string.IsNullOrWhiteSpace(s)))
        {
            Add(field, $"{field} is required.");
        }
    }

    public void MaxLength(string field, string? value, int maxLength)
    {
        if (value is not null && value.Length > maxLength)
        {
            Add(field, $"{field} must be at most {maxLength} characters.");
        }
    }

    public void OneOf(string field, string? value, IReadOnlyCollection<string> allowed)
    {
        if (value is not null && !allowed.Contains(value))
        {
            Add(field, $"{field} must be one of: {string.Join(", ", allowed)}.");
        }
    }

    public void AllOneOf(string field, IEnumerable<string>? values, IReadOnlyCollection<string> allowed)
    {
        if (values?.Any(v => !allowed.Contains(v)) == true)
        {
            Add(field, $"Every {field} value must be one of: {string.Join(", ", allowed)}.");
        }
    }

    public void Valid(string field, string? value, Func<string, bool> isValid, string expected)
    {
        if (value is not null && !isValid(value))
        {
            Add(field, $"{field} must be {expected}.");
        }
    }

    public IReadOnlyDictionary<string, string[]> ToDictionary() => _errors.ToDictionary(e => e.Key, e => e.Value.ToArray());
}

/// <summary>EF mapping for the common record model, shared by every generated static data entity.</summary>
public static class StaticModel
{
    public static void ConfigureCommon<TEntity>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity)
        where TEntity : StaticRecord
    {
        entity.HasKey(r => r.Id);
        entity.Property(r => r.Code).HasMaxLength(120);
        entity.Property(r => r.RecordStatus).HasMaxLength(20);
        entity.Property(r => r.RecordSource).HasMaxLength(60);
        entity.Property(r => r.RecordSourceVersion).HasMaxLength(30);
        entity.Property(r => r.RecordClass).HasMaxLength(20);
        entity.Property(r => r.CreatedBy).HasMaxLength(80);
        entity.Property(r => r.ApprovedBy).HasMaxLength(80);
        entity.Property(r => r.Labels).HasColumnType("jsonb");
        entity.Property(r => r.ExternalMappings).HasColumnType("jsonb");
        entity.Property(r => r.DisplayFlags).HasColumnType("jsonb");
    }
}
