using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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
            "SteamGameCustomStatus.Assets.GameCatalogs.NintendoSwitchTop100.json",
            "Local curated list")
    ]);

    public async Task<IReadOnlyList<GameNameSuggestion>> GetSuggestionsAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || maxResults <= 0 || _sources.Count == 0)
        {
            return Array.Empty<GameNameSuggestion>();
        }

        var distinctSuggestions = new Dictionary<string, GameNameSuggestion>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in _sources)
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
                continue;
            }

            foreach (var suggestion in sourceSuggestions)
            {
                if (!distinctSuggestions.ContainsKey(suggestion.Title))
                {
                    distinctSuggestions[suggestion.Title] = suggestion;
                }

                if (distinctSuggestions.Count >= maxResults)
                {
                    return distinctSuggestions.Values.ToArray();
                }
            }
        }

        return distinctSuggestions.Values.ToArray();
    }
}


