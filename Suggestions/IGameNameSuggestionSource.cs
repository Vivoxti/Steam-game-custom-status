namespace SteamGameCustomStatus.Suggestions;

internal interface IGameNameSuggestionSource
{
    Task<IReadOnlyList<GameNameSuggestion>> GetSuggestionsAsync(string query, int maxResults, CancellationToken cancellationToken);
}


