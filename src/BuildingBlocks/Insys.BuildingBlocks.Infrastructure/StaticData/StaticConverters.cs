using System.Net;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NodaTime;
using NodaTime.Text;
using NpgsqlTypes;

namespace Insys.BuildingBlocks.Infrastructure.StaticData;

/// <summary>
/// Converters for column types that have no JSON-friendly .NET type. The API uses strings: ISO 8601 durations for
/// interval ("P3M"), PostgreSQL range literals ("[18,30)"), and CIDR notation ("10.0.0.0/8").
/// </summary>
public static class StaticConverters
{
    public static readonly ValueConverter<string, Period> Duration = new(
        v => PeriodPattern.NormalizingIso.Parse(v).GetValueOrThrow(),
        v => PeriodPattern.NormalizingIso.Format(v));

    public static readonly ValueConverter<string, NpgsqlRange<int>> IntRange = new(
        v => NpgsqlRange<int>.Parse(v),
        v => v.ToString());

    public static readonly ValueConverter<string, NpgsqlRange<decimal>> DecimalRange = new(
        v => NpgsqlRange<decimal>.Parse(v),
        v => v.ToString());

    public static readonly ValueConverter<string[], IPNetwork[]> CidrArray = new(
        v => v.Select(IPNetwork.Parse).ToArray(),
        v => v.Select(n => n.ToString()).ToArray());

    // Variants for nullable columns. EF never passes null to a converter, so the null-forgiving operator is safe.
    public static readonly ValueConverter<string?, Period> OptionalDuration = new(
        v => PeriodPattern.NormalizingIso.Parse(v!).GetValueOrThrow(),
        v => PeriodPattern.NormalizingIso.Format(v));

    public static readonly ValueConverter<string?, NpgsqlRange<int>> OptionalIntRange = new(
        v => NpgsqlRange<int>.Parse(v!),
        v => v.ToString());

    public static readonly ValueConverter<string?, NpgsqlRange<decimal>> OptionalDecimalRange = new(
        v => NpgsqlRange<decimal>.Parse(v!),
        v => v.ToString());

    public static readonly ValueConverter<string[]?, IPNetwork[]> OptionalCidrArray = new(
        v => v!.Select(IPNetwork.Parse).ToArray(),
        v => v.Select(n => n.ToString()).ToArray());

    public static bool IsDuration(string value) => PeriodPattern.NormalizingIso.Parse(value).Success;

    public static bool IsIntRange(string value) => TryParse(() => NpgsqlRange<int>.Parse(value));

    public static bool IsDecimalRange(string value) => TryParse(() => NpgsqlRange<decimal>.Parse(value));

    public static bool IsCidr(string value) => IPNetwork.TryParse(value, out _);

    private static bool TryParse(Action parse)
    {
        try
        {
            parse();
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
