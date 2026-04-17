namespace SteamGameCustomStatus.Suggestions;

internal sealed record GameNameSuggestion(string Title, string Platform, string SourceLabel)
{
    public string Details => SourceLabel;
}

