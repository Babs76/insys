using System.Globalization;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Domain.Expressions;

namespace Insys.General.Domain.Underwriting;

public enum RiskObjectStatus
{
    Active,
    Inactive,
}

public sealed record GeneralRiskObjectRegistered(Guid RiskObjectId, string ObjectType, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Where a risk is: the subdivision, postal code and rating zone (RiskLocation, RiskZone).</summary>
public sealed record RiskLocation(string? Subdivision, string? PostalCode, string? RiskZone);

/// <summary>
/// The attribute schema of a risk object type (general.cfg_risk_object_types_and_attribute_schemas, GEN-005): each
/// attribute as <c>name:Type:Y|N</c> (mandatory), where Type is String(n), Integer, Decimal, Boolean, Date or Ref X,
/// plus validation conditions over the attributes (ADR-008), such as the catalogue's <c>year &lt;= current_year + 1</c>.
/// <c>current_year</c> and <c>today()</c> are the evaluation date's.
/// </summary>
public sealed class AttributeSchema
{
    private readonly IReadOnlyList<(string Name, string Type, int? Length, bool Mandatory)> _attributes;
    private readonly IReadOnlyList<Expression> _rules;

    private AttributeSchema(string objectType, int version, IReadOnlyList<(string, string, int?, bool)> attributes, IReadOnlyList<Expression> rules)
    {
        ObjectType = objectType;
        Version = version;
        _attributes = attributes;
        _rules = rules;
    }

    public string ObjectType { get; }

    /// <summary>The version of the type's configuration row, stored with the risk object (SchemaVersion).</summary>
    public int Version { get; }

    public static Result<AttributeSchema> Parse(string objectType, int version, IReadOnlyList<string> attributes, IReadOnlyList<string> validation)
    {
        var parsed = new List<(string, string, int?, bool)>();
        foreach (var definition in attributes)
        {
            var parts = definition.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length != 3 || parts[0].Length == 0)
            {
                return Error.Validation(GeneralErrorCodes.AttributeSchemaInvalid, $"{objectType}: '{definition}' is not name:Type:Y|N.");
            }

            var type = parts[1];
            int? length = null;
            var open = type.IndexOf('(', StringComparison.Ordinal);
            if (open > 0 && type.EndsWith(')') && int.TryParse(type[(open + 1)..^1], CultureInfo.InvariantCulture, out var n))
            {
                length = n;
                type = type[..open];
            }

            parsed.Add((parts[0], type.StartsWith("Ref", StringComparison.Ordinal) ? "Ref" : type, length, parts[2].Equals("Y", StringComparison.OrdinalIgnoreCase)));
        }

        var rules = new List<Expression>();
        foreach (var text in validation)
        {
            var rule = Expression.Parse(text);
            if (rule.IsFailure)
            {
                return Error.Validation(GeneralErrorCodes.AttributeSchemaInvalid, $"{objectType}: validation '{text}': {rule.Error.Message}");
            }

            rules.Add(rule.Value);
        }

        return new AttributeSchema(objectType, version, parsed, rules);
    }

    /// <summary>Attributes conform to the type's schema (GEN-005): every mandatory one present, each of its type, no unknown ones, and every validation holds.</summary>
    public Result Validate(IReadOnlyDictionary<string, object?> values, DateOnly today)
    {
        var problems = new List<string>();
        foreach (var name in values.Keys.Where(k => _attributes.All(a => a.Name != k)))
        {
            problems.Add($"{name} is not an attribute of {ObjectType}.");
        }

        foreach (var (name, type, length, mandatory) in _attributes)
        {
            if (!values.TryGetValue(name, out var value) || value is null)
            {
                if (mandatory)
                {
                    problems.Add($"{name} is required.");
                }

                continue;
            }

            var ok = type switch
            {
                "String" or "Ref" => value is string s && (length is null || s.Length <= length),
                "Integer" => value is decimal d && decimal.Truncate(d) == d,
                "Decimal" => value is decimal,
                "Boolean" => value is bool,
                "Date" => value is string text && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                _ => false,
            };
            if (!ok)
            {
                problems.Add($"{name} is not a valid {type}{(length is null ? "" : $"({length})")}.");
            }
        }

        if (problems.Count == 0)
        {
            var variables = values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
            variables["current_year"] = (decimal)today.Year;
            foreach (var rule in _rules)
            {
                var holds = rule.Evaluate(variables, today: today);
                if (holds.IsFailure || !holds.Value)
                {
                    problems.Add(holds.IsFailure ? holds.Error.Message : "A validation rule of the object type does not hold.");
                }
            }
        }

        return problems.Count == 0
            ? Result.Success()
            : Error.Validation(GeneralErrorCodes.AttributesInvalid, string.Join(" ", problems));
    }
}

/// <summary>
/// The GeneralRiskObject aggregate (general.risk_object): a vehicle, building, vessel or other insured object whose
/// attributes conform to its type's schema version (GEN-005), with a valuation basis whenever a value is set.
/// </summary>
public sealed class RiskObject : AggregateRoot<Guid>
{
    private RiskObject()
    {
    }

    public string ObjectType { get; private set; } = "";

    /// <summary>The attributes as JSON, already validated against <see cref="AttributeSchemaVersion"/>.</summary>
    public string Attributes { get; private set; } = "{}";

    public int AttributeSchemaVersion { get; private set; }

    public decimal? Valuation { get; private set; }

    public string? ValuationBasis { get; private set; }

    public string? Subdivision { get; private set; }

    public string? PostalCode { get; private set; }

    public string? RiskZone { get; private set; }

    public RiskObjectStatus Status { get; private set; }

    public static Result<RiskObject> RegisterRiskObject(Guid id, AttributeSchema schema, IReadOnlyDictionary<string, object?> values, string attributesJson,
        decimal? valuation, string? valuationBasis, RiskLocation location, DateOnly today, DateTimeOffset now)
    {
        var valid = schema.Validate(values, today);
        var valued = CheckValuation(valuation, valuationBasis);
        if (valid.IsFailure || valued.IsFailure)
        {
            return valid.IsFailure ? valid.Error! : valued.Error!;
        }

        var risk = new RiskObject
        {
            Id = Guard.NotEmpty(id), ObjectType = Guard.MaxLength(schema.ObjectType, 15), Attributes = attributesJson, AttributeSchemaVersion = schema.Version,
            Valuation = valuation, ValuationBasis = valuationBasis, Subdivision = location.Subdivision, PostalCode = location.PostalCode, RiskZone = location.RiskZone,
            Status = RiskObjectStatus.Active,
        };
        risk.Raise(new GeneralRiskObjectRegistered(id, risk.ObjectType, now));
        return risk;
    }

    /// <summary>Command UpdateAttributes: validated against the type's schema version in force now, which is recorded.</summary>
    public Result UpdateAttributes(AttributeSchema schema, IReadOnlyDictionary<string, object?> values, string attributesJson, DateOnly today)
    {
        if (schema.ObjectType != ObjectType)
        {
            return Error.Validation(GeneralErrorCodes.AttributesInvalid, $"The risk object is a {ObjectType}, not a {schema.ObjectType}.");
        }

        var valid = schema.Validate(values, today);
        if (valid.IsFailure)
        {
            return valid;
        }

        Attributes = attributesJson;
        AttributeSchemaVersion = schema.Version;
        MarkChanged();
        return Result.Success();
    }

    /// <summary>Command RevalueRiskObject.</summary>
    public Result RevalueRiskObject(decimal? valuation, string? valuationBasis)
    {
        var valued = CheckValuation(valuation, valuationBasis);
        if (valued.IsFailure)
        {
            return valued;
        }

        Valuation = valuation;
        ValuationBasis = valuationBasis;
        MarkChanged();
        return Result.Success();
    }

    /// <summary>Transition "No longer insured".</summary>
    public Result Deactivate()
    {
        if (Status == RiskObjectStatus.Inactive)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "The risk object is already inactive.");
        }

        Status = RiskObjectStatus.Inactive;
        MarkChanged();
        return Result.Success();
    }

    private static Result CheckValuation(decimal? valuation, string? basis) =>
        valuation is null || (valuation >= 0 && !string.IsNullOrWhiteSpace(basis))
            ? Result.Success()
            : Error.Validation(GeneralErrorCodes.ValuationBasisRequired, "A valuation is not negative and has a valuation basis.");
}
