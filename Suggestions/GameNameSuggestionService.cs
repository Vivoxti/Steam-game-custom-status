namespace SteamGameCustomStatus.Suggestions;

internal sealed class GameNameSuggestionService
{
    private readonly IReadOnlyList<IGameNameSuggestionSource> _sources;

    public GameNameSuggestionService(IEnumerable<IGameNameSuggestionSource> sources)
    {
        _sources = sources.ToArray();
    }

    public static GameNameSuggestionService Default { get; } = new([
        new EmbeddedCatalogSuggestionSource(
            "SteamGameCustomStatus.Assets.GameCatalogs.ConsoleExclusivesTop200.json",
            "Offline curated list"),
        new SteamStoreSuggestionSource()
    ]);

    public Task<IReadOnlyList<GameNameSuggestion>> GetOfflineSuggestionsAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        return GetSuggestionsCoreAsync(query, maxResults, includeOnlineSources: false, cancellationToken);
    }

    public async Task<IReadOnlyList<GameNameSuggestion>> GetSuggestionsAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        return await GetSuggestionsCoreAsync(query, maxResults, includeOnlineSources: true, cancellationToken);
    }

    private async Task<IReadOnlyList<GameNameSuggestion>> GetSuggestionsCoreAsync(
        string query,
        int maxResults,
        bool includeOnlineSources,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || maxResults <= 0 || _sources.Count == 0)
        {
            return Array.Empty<GameNameSuggestion>();
        }

        var activeSources = includeOnlineSources
            ? _sources
            : _sources.Where(source => !source.IsOnline).ToArray();

        if (activeSources.Count == 0)
        {
            return Array.Empty<GameNameSuggestion>();
        }

        var suggestionSets = new List<IReadOnlyList<GameNameSuggestion>>(activeSources.Count);

        foreach (var source in activeSources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<GameNameSuggestion> sourceSuggestions;
            try
            {
                sourceSuggestions = await source.GetSuggestionsAsync(query, maxResults, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                suggestionSets.Add(Array.Empty<GameNameSuggestion>());
                continue;
            }

            suggestionSets.Add(sourceSuggestions);
        }

        return MergeSuggestions(suggestionSets, maxResults);
    }

    private static IReadOnlyList<GameNameSuggestion> MergeSuggestions(
        IReadOnlyList<IReadOnlyList<GameNameSuggestion>> suggestionSets,
        int maxResults)
    {
        if (suggestionSets.Count == 0 || maxResults <= 0)
        {
            return Array.Empty<GameNameSuggestion>();
        }

        var mergedSuggestions = new List<GameNameSuggestion>(maxResults);
        var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var positions = new int[suggestionSets.Count];

        while (mergedSuggestions.Count < maxResults)
        {
            var addedAnySuggestionThisRound = false;

            for (var sourceIndex = 0; sourceIndex < suggestionSets.Count && mergedSuggestions.Count < maxResults; sourceIndex++)
            {
                var suggestions = suggestionSets[sourceIndex];
                while (positions[sourceIndex] < suggestions.Count)
                {
                    var suggestion = suggestions[positions[sourceIndex]++];
                    if (!seenTitles.Add(suggestion.Title))
                    {
                        continue;
                    }

                    mergedSuggestions.Add(suggestion);
                    addedAnySuggestionThisRound = true;
                    break;
                }
            }

            if (!addedAnySuggestionThisRound)
            {
                break;
            }
        }

        return mergedSuggestions;
    }
}


