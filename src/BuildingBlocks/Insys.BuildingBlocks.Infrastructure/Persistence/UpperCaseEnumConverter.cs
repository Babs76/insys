using System.Text;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Insys.BuildingBlocks.Infrastructure.Persistence;

/// <summary>Stores an enum as an upper-case code, the schema's convention: PendingApproval becomes PENDING_APPROVAL.</summary>
public sealed class UpperCaseEnumConverter<TEnum>() : ValueConverter<TEnum, string>(
    v => ToCode(v),
    v => FromCode(v))
    where TEnum : struct, Enum
{
    private static readonly Dictionary<string, TEnum> ByCode = Enum.GetValues<TEnum>().ToDictionary(ToCode, v => v, StringComparer.Ordinal);

    public static string ToCode(TEnum value) => SnakeCase.From(value.ToString()).ToUpperInvariant();

    public static TEnum FromCode(string code) =>
        ByCode.TryGetValue(code, out var value) ? value : throw new InvalidOperationException($"Unknown {typeof(TEnum).Name} code '{code}'.");
}
