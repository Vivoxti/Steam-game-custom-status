using SteamGameCustomStatus.UI.Dialogs;
using SteamGameCustomStatus.UI.Windows;
using Wpf = System.Windows;

namespace SteamGameCustomStatus.Workflows;

internal static class NintendoSetupWorkflow
{
    public static void Run(Wpf.Window? owner)
    {
        var controller = (Wpf.Application.Current as App)?.SwitchPresence;
        if (controller is null)
        {
            const string message = "Nintendo Switch support is not available right now.";
            if (owner is MainWindow mainWindow)
            {
                mainWindow.ShowInlineMessage(message, isWarning: true);
            }
            else
            {
                Wpf.MessageBox.Show(message, "Steam Game Custom Status", Wpf.MessageBoxButton.OK, Wpf.MessageBoxImage.Warning);
            }

            return;
        }

        var dialog = new NintendoSetupDialog(controller, owner);
        dialog.ShowDialog();
    }
}
