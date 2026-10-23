using System.Globalization;

namespace Insys.BuildingBlocks.Application.Localisation;

/// <summary>
/// A tenant's supported languages (tenant.cfg_locale_defaults.languages in force). The first is the tenant's default
/// (docs/specs/addenda/localisation.md §1, OQ-068). A tenant without locale defaults supports English only.
/// </summary>
public sealed record TenantLanguages(IReadOnlyList<string> Supported)
{
    public const string English = "en";

    public static readonly TenantLanguages EnglishOnly = new([English]);

    public string Default => Supported.Count > 0 ? Supported[0] : English;

    public bool Supports(string language) => Supported.Contains(language, StringComparer.Ordinal);
}

/// <summary>Public query interface: a tenant's languages, implemented by the Tenancy module.</summary>
public interface ILanguageSettings
{
    Task<TenantLanguages> For(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>
/// The texts of the API message catalogue in one language (channel.ref_ui_label_and_translation_bundles, keyed by the error
/// code catalogue's message_key), by error code: titles from "&lt;message_key&gt;", details from "&lt;message_key&gt;.detail".
/// </summary>
public sealed record MessageTexts(IReadOnlyDictionary<string, string> Titles, IReadOnlyDictionary<string, string> Details)
{
    public static readonly MessageTexts None = new(new Dictionary<string, string>(), new Dictionary<string, string>());
}

/// <summary>Public query interface: the API message catalogue, implemented by the Integration module, which owns it.</summary>
public interface IMessageCatalogue
{
    /// <summary>The texts in one language, the tenant's own rows over the global ones; global rows only without a tenant.</summary>
    Task<MessageTexts> Texts(Guid? tenantId, string language, CancellationToken cancellationToken);
}

/// <summary>Language resolution and the text fallback chain (addendum §2).</summary>
public static class LanguageChain
{
    /// <summary>The primary language subtags of an Accept-Language header, by quality then order; "*" and malformed entries are ignored.</summary>
    public static IReadOnlyList<string> Parse(string? acceptLanguage)
    {
        if (string.IsNullOrWhiteSpace(acceptLanguage))
        {
            return [];
        }

        return acceptLanguage.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select((entry, index) =>
            {
                var parts = entry.Split(';', StringSplitOptions.TrimEntries);
                var quality = 1.0;
                foreach (var parameter in parts.Skip(1))
                {
                    if (parameter.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                        && double.TryParse(parameter[2..], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var q))
                    {
                        quality = q;
                    }
                }

                var primary = parts[0].Split('-')[0].ToLowerInvariant();
                return (Language: primary, Quality: quality, Index: index);
            })
            .Where(e => e.Quality > 0 && e.Language.Length == 2 && e.Language.All(char.IsAsciiLetterLower))
            .OrderByDescending(e => e.Quality).ThenBy(e => e.Index)
            .Select(e => e.Language).Distinct().ToList();
    }

    /// <summary>The first requested language the tenant supports, else the tenant's default.</summary>
    public static string Resolve(IEnumerable<string> requested, TenantLanguages tenant) =>
        requested.FirstOrDefault(tenant.Supports) ?? tenant.Default;

    /// <summary>The fallback chain: the preferred language, the tenant's default, then English, without repeats.</summary>
    public static IReadOnlyList<string> Chain(string? preferred, TenantLanguages tenant) =>
        new[] { preferred, tenant.Default, TenantLanguages.English }.OfType<string>().Distinct(StringComparer.Ordinal).ToList();

    /// <summary>The first language of the chain that has a text, and that text.</summary>
    public static (string Language, string Text)? Pick(IReadOnlyList<string> chain, Func<string, string?> textFor)
    {
        foreach (var language in chain)
        {
            if (textFor(language) is { Length: > 0 } text)
            {
                return (language, text);
            }
        }

        return null;
    }
}

/// <summary>
/// The language of the current request (addendum §2): resolved once from Accept-Language and the tenant's languages, with
/// the catalogue texts of its fallback chain. English, with no texts, until the API initialises it.
/// </summary>
public sealed class RequestLanguage
{
    public string Language { get; private set; } = TenantLanguages.English;

    public IReadOnlyList<string> Chain { get; private set; } = [TenantLanguages.English];

    /// <summary>The catalogue texts of each language in the chain, in chain order.</summary>
    public IReadOnlyList<(string Language, MessageTexts Texts)> Messages { get; private set; } = [];

    public void Initialise(string language, IReadOnlyList<string> chain, IReadOnlyList<(string Language, MessageTexts Texts)> messages)
    {
        Language = language;
        Chain = chain;
        Messages = messages;
    }
}
