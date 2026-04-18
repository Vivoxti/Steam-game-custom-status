using SteamGameCustomStatus.Steam;
using SteamGameCustomStatus.UI.Windows;
using Wpf = System.Windows;

namespace SteamGameCustomStatus.Workflows;

internal static class OpenSteamAddGameWorkflow
{
    public static void Run(Wpf.Window? owner)
    {
        var result = SteamShortcutRenamer.OpenSteamForAddingCurrentExecutable();
        var image = result.Success ? Wpf.MessageBoxImage.Information : Wpf.MessageBoxImage.Warning;

        if (owner is MainWindow mainWindow)
        {
            mainWindow.ShowInlineMessage(result.Message, isWarning: !result.Success);
            return;
        }

        if (owner is not null)
        {
            Wpf.MessageBox.Show(owner, result.Message, "Steam Game Custom Status", Wpf.MessageBoxButton.OK, image);
            return;
        }

        Wpf.MessageBox.Show(result.Message, "Steam Game Custom Status", Wpf.MessageBoxButton.OK, image);
    }
}

