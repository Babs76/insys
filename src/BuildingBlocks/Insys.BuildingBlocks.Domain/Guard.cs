using System.Runtime.CompilerServices;

namespace Insys.BuildingBlocks.Domain;

/// <summary>Guards against programming errors. Business rule failures return <see cref="Result"/> instead.</summary>
public static class Guard
{
    public static T NotNull<T>(T? value, [CallerArgumentExpression(nameof(value))] string? name = null)
        where T : class =>
        value ?? throw new ArgumentNullException(name);

    public static string NotNullOrWhiteSpace(string? value, [CallerArgumentExpression(nameof(value))] string? name = null) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value;

    public static string MaxLength(string value, int maxLength, [CallerArgumentExpression(nameof(value))] string? name = null) =>
        value.Length > maxLength ? throw new ArgumentException($"Value exceeds {maxLength} characters.", name) : value;

    public static Guid NotEmpty(Guid value, [CallerArgumentExpression(nameof(value))] string? name = null) =>
        value == Guid.Empty ? throw new ArgumentException("Value must not be empty.", name) : value;

    public static decimal NotNegative(decimal value, [CallerArgumentExpression(nameof(value))] string? name = null) =>
        value < 0 ? throw new ArgumentOutOfRangeException(name, value, "Value must not be negative.") : value;
}
