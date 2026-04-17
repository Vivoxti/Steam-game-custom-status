namespace SteamGameCustomStatus.Suggestions;

internal interface IGameNameSuggestionSource
{
    bool IsOnline { get; }

    Task<IReadOnlyList<GameNameSuggestion>> GetSuggestionsAsync(string query, int maxResults, CancellationToken cancellationToken);
}


