using System.Windows;
using WpfInput = System.Windows.Input;

namespace SteamGameCustomStatus.UI.Dialogs;

public partial class RenameDialog : Window
{
    public string? ResultName { get; private set; }

    public RenameDialog(string currentName, Window? owner)
    {
        InitializeComponent();

        NameTextBox.Text = currentName;
        NameTextBox.SelectAll();

        if (owner is { IsVisible: true, WindowState: not WindowState.Minimized })
        {
            Owner = owner;
            PositionBelowOwner(owner);
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        Loaded += (_, _) =>
        {
            NameTextBox.Focus();
            NameTextBox.SelectAll();
        };
    }

    private void PositionBelowOwner(Window owner)
    {
        // Place the dialog centered horizontally under the owner window with a small visual gap.
        // Owner uses Margin="12" for the shadow border, so the visible area is inset by 12px.
        // This dialog also has Margin="12" shadow border.
        const double shadowMargin = 12;
        const double gapBelowOwner = 10;

        var ownerVisibleLeft = owner.Left + shadowMargin;
        var ownerVisibleWidth = owner.Width - shadowMargin * 2;
        var ownerVisibleBottom = owner.Top + owner.Height - shadowMargin;

        var dialogVisibleWidth = Width - shadowMargin * 2;

        Left = ownerVisibleLeft + (ownerVisibleWidth - dialogVisibleWidth) / 2 - shadowMargin;
        Top = ownerVisibleBottom - shadowMargin + gapBelowOwner;
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        Accept();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        ResultName = null;
        DialogResult = false;
    }

    private void NameTextBox_KeyDown(object sender, WpfInput.KeyEventArgs e)
    {
        if (e.Key == WpfInput.Key.Enter)
        {
            Accept();
            e.Handled = true;
        }
        else if (e.Key == WpfInput.Key.Escape)
        {
            ResultName = null;
            DialogResult = false;
            e.Handled = true;
        }
    }

    private void Accept()
    {
        var text = NameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        ResultName = text;
        DialogResult = true;
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, WpfInput.MouseButtonEventArgs e)
    {
        if (e.ButtonState == WpfInput.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}





