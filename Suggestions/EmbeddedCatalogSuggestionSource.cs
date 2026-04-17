using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamGameCustomStatus.Suggestions;

internal sealed class EmbeddedCatalogSuggestionSource : IGameNameSuggestionSource
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Regex NonLetterOrDigitRegex = new("[^\\p{L}\\p{Nd}]+", RegexOptions.Compiled);
    private readonly string _resourceName;
    private readonly string _sourceLabel;
    private readonly Lazy<IReadOnlyList<CatalogEntry>> _catalog;

    public bool IsOnline => false;

    public EmbeddedCatalogSuggestionSource(string resourceName, string sourceLabel)
    {
        _resourceName = resourceName;
        _sourceLabel = sourceLabel;
        _catalog = new Lazy<IReadOnlyList<CatalogEntry>>(LoadCatalog);
    }

    public Task<IReadOnlyList<GameNameSuggestion>> GetSuggestionsAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(query) || maxResults <= 0)
        {
            return Task.FromResult<IReadOnlyList<GameNameSuggestion>>(Array.Empty<GameNameSuggestion>());
        }

        var trimmedQuery = query.Trim();
        if (trimmedQuery.Length < 2)
        {
            return Task.FromResult<IReadOnlyList<GameNameSuggestion>>(Array.Empty<GameNameSuggestion>());
        }

        var normalizedQuery = Normalize(trimmedQuery);
        if (normalizedQuery.Length == 0)
        {
            return Task.FromResult<IReadOnlyList<GameNameSuggestion>>(Array.Empty<GameNameSuggestion>());
        }

        var suggestions = _catalog.Value
            .Select(entry => new
            {
                Entry = entry,
                Score = Score(entry.Title, normalizedQuery)
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Entry.Title.Length)
            .ThenBy(candidate => candidate.Entry.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(maxResults)
            .Select(candidate => new GameNameSuggestion(candidate.Entry.Title, candidate.Entry.Platform, _sourceLabel))
            .ToArray();

        return Task.FromResult<IReadOnlyList<GameNameSuggestion>>(suggestions);
    }

    private IReadOnlyList<CatalogEntry> LoadCatalog()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(_resourceName);
            if (stream is null)
            {
                return Array.Empty<CatalogEntry>();
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var json = reader.ReadToEnd();
            var entries = JsonSerializer.Deserialize<List<CatalogEntry>>(json, JsonOptions);
            if (entries is null || entries.Count == 0)
            {
                return Array.Empty<CatalogEntry>();
            }

            return entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Title))
                .Select(entry => new CatalogEntry(
                    entry.Title.Trim(),
                    string.IsNullOrWhiteSpace(entry.Platform) ? "Console exclusive" : entry.Platform.Trim()))
                .ToArray();
        }
        catch
        {
            return Array.Empty<CatalogEntry>();
        }
    }

    private static int Score(string title, string normalizedQuery)
    {
        var normalizedTitle = Normalize(title);
        if (normalizedTitle.Length == 0)
        {
            return 0;
        }

        if (string.Equals(normalizedTitle, normalizedQuery, StringComparison.Ordinal))
        {
            return 10_000;
        }

        if (normalizedTitle.StartsWith(normalizedQuery, StringComparison.Ordinal))
        {
            return 8_000 - (normalizedTitle.Length - normalizedQuery.Length);
        }

        var wordIndex = normalizedTitle.IndexOf(" " + normalizedQuery, StringComparison.Ordinal);
        if (wordIndex >= 0)
        {
            return 6_500 - wordIndex;
        }

        var containsIndex = normalizedTitle.IndexOf(normalizedQuery, StringComparison.Ordinal);
        if (containsIndex >= 0)
        {
            return 5_000 - containsIndex;
        }

        var queryWords = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (queryWords.Length == 0)
        {
            return 0;
        }

        var matchedWords = 0;
        foreach (var queryWord in queryWords)
        {
            if (normalizedTitle.Contains(queryWord, StringComparison.Ordinal))
            {
                matchedWords++;
            }
        }

        return matchedWords == queryWords.Length
            ? 3_000 + matchedWords * 100 - normalizedTitle.Length
            : 0;
    }

    private static string Normalize(string value)
    {
        var collapsed = NonLetterOrDigitRegex.Replace(value, " ");
        return string.Join(' ', collapsed
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToLowerInvariant();
    }

    private sealed record CatalogEntry(string Title, string Platform);
}


