using Wpf = System.Windows;

namespace SteamGameCustomStatus;

internal static class OpenSteamAddGameWorkflow
{
    public static void Run(Wpf.Window? owner)
    {
        var result = SteamShortcutRenamer.OpenSteamForAddingCurrentExecutable();
        var image = result.Success ? Wpf.MessageBoxImage.Information : Wpf.MessageBoxImage.Warning;

        if (owner is not null)
        {
            Wpf.MessageBox.Show(owner, result.Message, "Steam Game Custom Status", Wpf.MessageBoxButton.OK, image);
            return;
        }

        Wpf.MessageBox.Show(result.Message, "Steam Game Custom Status", Wpf.MessageBoxButton.OK, image);
    }
}

