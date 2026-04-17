using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;

namespace SteamGameCustomStatus.Suggestions;

internal sealed class SteamStoreSuggestionSource : IGameNameSuggestionSource
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(1200);
    private static readonly StringComparer QueryComparer = StringComparer.Ordinal;

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(QueryComparer);

    public bool IsOnline => true;

    public async Task<IReadOnlyList<GameNameSuggestion>> GetSuggestionsAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(query) || maxResults <= 0)
        {
            return Array.Empty<GameNameSuggestion>();
        }

        var trimmedQuery = query.Trim();
        if (trimmedQuery.Length < 2)
        {
            return Array.Empty<GameNameSuggestion>();
        }

        var normalizedQuery = NormalizeQuery(trimmedQuery);
        if (normalizedQuery.Length < 2)
        {
            return Array.Empty<GameNameSuggestion>();
        }

        if (TryGetCached(normalizedQuery, requireFresh: true, out var cachedSuggestions))
        {
            return LimitResults(cachedSuggestions, maxResults);
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(RequestTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildRequestUri(trimmedQuery));
            request.Headers.Accept.ParseAdd("application/json");

            using var response = await SharedHttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCancellation.Token);

            if (!response.IsSuccessStatusCode)
            {
                return GetFallbackSuggestions(normalizedQuery, maxResults);
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(timeoutCancellation.Token);
            var payload = await JsonSerializer.DeserializeAsync<StoreSearchResponse>(responseStream, JsonOptions, timeoutCancellation.Token);
            var suggestions = payload?.Items?
                .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                .Select(item => item.Name!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(maxResults)
                .Select(title => new GameNameSuggestion(title, "PC", "Steam Store"))
                .ToArray() ?? Array.Empty<GameNameSuggestion>();

            _cache[normalizedQuery] = new CacheEntry(DateTimeOffset.UtcNow, suggestions);
            TrimCacheIfNeeded();

            return suggestions;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GetFallbackSuggestions(normalizedQuery, maxResults);
        }
        catch (HttpRequestException)
        {
            return GetFallbackSuggestions(normalizedQuery, maxResults);
        }
        catch (JsonException)
        {
            return GetFallbackSuggestions(normalizedQuery, maxResults);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("SteamGameCustomStatus/1.0");
        return client;
    }

    private static Uri BuildRequestUri(string query)
    {
        return new Uri($"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(query)}&l=english&cc=US");
    }

    private IReadOnlyList<GameNameSuggestion> GetFallbackSuggestions(string normalizedQuery, int maxResults)
    {
        return TryGetCached(normalizedQuery, requireFresh: false, out var cachedSuggestions)
            ? LimitResults(cachedSuggestions, maxResults)
            : Array.Empty<GameNameSuggestion>();
    }

    private bool TryGetCached(string normalizedQuery, bool requireFresh, out IReadOnlyList<GameNameSuggestion> suggestions)
    {
        if (_cache.TryGetValue(normalizedQuery, out var cacheEntry))
        {
            var age = DateTimeOffset.UtcNow - cacheEntry.StoredAtUtc;
            if (!requireFresh || age <= CacheLifetime)
            {
                suggestions = cacheEntry.Suggestions;
                return true;
            }
        }

        suggestions = Array.Empty<GameNameSuggestion>();
        return false;
    }

    private void TrimCacheIfNeeded()
    {
        const int maxCacheEntries = 128;
        const int targetCacheEntries = 96;

        if (_cache.Count <= maxCacheEntries)
        {
            return;
        }

        foreach (var staleEntry in _cache
                     .OrderBy(pair => pair.Value.StoredAtUtc)
                     .Take(Math.Max(0, _cache.Count - targetCacheEntries)))
        {
            _cache.TryRemove(staleEntry.Key, out _);
        }
    }

    private static IReadOnlyList<GameNameSuggestion> LimitResults(IReadOnlyList<GameNameSuggestion> suggestions, int maxResults)
    {
        return suggestions.Count <= maxResults
            ? suggestions
            : suggestions.Take(maxResults).ToArray();
    }

    private static string NormalizeQuery(string value)
    {
        return string.Join(' ', value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToLowerInvariant();
    }

    private sealed record CacheEntry(DateTimeOffset StoredAtUtc, IReadOnlyList<GameNameSuggestion> Suggestions);

    private sealed class StoreSearchResponse
    {
        public List<StoreSearchItem>? Items { get; init; }
    }

    private sealed class StoreSearchItem
    {
        public string? Name { get; init; }
    }
}

