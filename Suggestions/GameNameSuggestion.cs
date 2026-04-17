namespace SteamGameCustomStatus.Suggestions;

internal sealed record GameNameSuggestion(string Title, string Platform, string SourceLabel)
{
    public string Details => string.IsNullOrWhiteSpace(Platform)
        ? SourceLabel
        : $"{Platform} • {SourceLabel}";
}

