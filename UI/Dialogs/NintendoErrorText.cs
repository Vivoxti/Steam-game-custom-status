namespace SteamGameCustomStatus.UI.Dialogs;

/// <summary>
/// Maps nxapi-bridge error codes to short user-facing text.
/// </summary>
internal static class NintendoErrorText
{
    public static string Describe(string? errorCode, string? message)
    {
        return errorCode switch
        {
            "not_logged_in" => "The bot account is not signed in. Sign in to continue.",
            "auth_failed" => "Nintendo sign-in failed. Try signing in with the bot account again.",
            "network" => "Could not reach Nintendo. Check your connection and try again.",
            "bridge_missing" => "This build does not include the Nintendo bridge.",
            "timeout" => "Nintendo took too long to respond. Try again.",
            _ => string.IsNullOrWhiteSpace(message) ? "Something went wrong while talking to Nintendo." : message.Trim()
        };
    }
}
