using SteamGameCustomStatus.Nintendo;
using Controls = System.Windows.Controls;
using Media = System.Windows.Media;
using Wpf = System.Windows;
using WpfInput = System.Windows.Input;

namespace SteamGameCustomStatus.UI.Dialogs;

public partial class NintendoSetupDialog
{
    private readonly SwitchPresenceController _controller;
    private readonly CancellationTokenSource _lifetime = new();

    private NintendoAccount? _bot;
    private string? _selectedNsaId;
    private string? _selectedName;
    private IReadOnlyList<NintendoFriend>? _friends;
    private string? _friendsHint;
    private bool _suppressSelectionChanged;
    private bool _isClosed;
    private bool _botChecked;
    private int _busyCount;

    internal NintendoSetupDialog(SwitchPresenceController controller, Wpf.Window? owner)
    {
        InitializeComponent();

        _controller = controller;

        var settings = controller.Settings;
        _selectedNsaId = string.IsNullOrWhiteSpace(settings.WatchedNsaId) ? null : settings.WatchedNsaId;
        _selectedName = settings.WatchedName;
        EnabledCheckBox.IsChecked = settings.Enabled;
        SelectInterval(settings.PollIntervalSeconds);

        if (owner is { IsVisible: true, WindowState: not Wpf.WindowState.Minimized })
        {
            Owner = owner;
            WindowStartupLocation = Wpf.WindowStartupLocation.CenterOwner;
        }

        PopulateFriends(null);
        UpdateBotUi();

        Loaded += NintendoSetupDialog_Loaded;
        Closed += NintendoSetupDialog_Closed;
    }

    private async void NintendoSetupDialog_Loaded(object sender, Wpf.RoutedEventArgs e)
    {
        if (!NxapiBridge.IsEmbedded)
        {
            ShowMessage("This build does not include the Nintendo bridge, so signing in is unavailable.", isWarning: true);
            UpdateBotUi();
            PopulateFriends(null);
            return;
        }

        await LoadBotAndFriendsAsync();
    }

    private void NintendoSetupDialog_Closed(object? sender, EventArgs e)
    {
        _isClosed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    // ---------------------------------------------------------------- loading

    private async Task LoadBotAndFriendsAsync()
    {
        BeginBusy();
        try
        {
            var status = await NxapiBridge.GetStatusAsync(_lifetime.Token);
            if (_isClosed)
            {
                return;
            }

            _botChecked = true;

            if (!status.Success)
            {
                _bot = null;
                ShowMessage(NintendoErrorText.Describe(status.ErrorCode, status.Message), isWarning: true);
                BotDetailOverride = "Status unavailable";
                return;
            }

            BotDetailOverride = null;
            _bot = status.Value;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            EndBusy();
        }

        await LoadFriendsAsync();
    }

    private async Task LoadFriendsAsync()
    {
        if (_bot is null)
        {
            _friends = null;
            _friendsHint = "Sign in the bot account to see its friends.";
            PopulateFriends(null);
            return;
        }

        BeginBusy();
        try
        {
            _friendsHint = "Loading friends…";
            PopulateFriends(null);

            var result = await NxapiBridge.GetFriendsAsync(_lifetime.Token);
            if (_isClosed)
            {
                return;
            }

            if (result.Success)
            {
                _friends = result.Value;
                _friendsHint = result.Value is { Count: > 0 }
                    ? null
                    : "Add the bot account as a friend on your Switch, then refresh.";
            }
            else
            {
                _friends = null;
                _friendsHint = "Could not load the friend list.";
                if (result.ErrorCode == "not_logged_in")
                {
                    _bot = null;
                }

                ShowMessage(NintendoErrorText.Describe(result.ErrorCode, result.Message), isWarning: true);
            }

            PopulateFriends(_friends);
        }
        catch (OperationCanceledException)
        {
            // dialog closed
        }
        finally
        {
            EndBusy();
        }
    }

    // ---------------------------------------------------------------- UI state

    private string? BotDetailOverride { get; set; }

    private void BeginBusy()
    {
        _busyCount++;
        UpdateBotUi();
    }

    private void EndBusy()
    {
        _busyCount = Math.Max(0, _busyCount - 1);
        if (!_isClosed)
        {
            UpdateBotUi();
        }
    }

    private bool IsBusy => _busyCount > 0;

    private void UpdateBotUi()
    {
        var embedded = NxapiBridge.IsEmbedded;

        if (!embedded)
        {
            BotNameText.Text = "Unavailable";
            BotDetailText.Text = "This build does not include the Nintendo bridge.";
        }
        else if (_bot is not null)
        {
            BotNameText.Text = _bot.DisplayName;
            BotDetailText.Text = "Signed in";
        }
        else if (!_botChecked)
        {
            BotNameText.Text = "Checking…";
            BotDetailText.Text = string.Empty;
        }
        else
        {
            BotNameText.Text = "Not signed in";
            BotDetailText.Text = BotDetailOverride ?? "Sign in with a separate bot Nintendo account.";
        }

        SignInButton.Visibility = _bot is null ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        SignOutButton.Visibility = _bot is null ? Wpf.Visibility.Collapsed : Wpf.Visibility.Visible;
        SignInButton.IsEnabled = embedded && !IsBusy;
        SignOutButton.IsEnabled = embedded && !IsBusy;
        RefreshButton.IsEnabled = embedded && _bot is not null && !IsBusy;
        SaveButton.IsEnabled = !IsBusy;
        FriendsListBox.IsEnabled = !IsBusy;
    }

    private void ShowMessage(string message, bool isWarning)
    {
        MessageCard.Background = (Media.Brush)FindResource(isWarning ? "WarningBg" : "SuccessBg");
        MessageCard.BorderBrush = (Media.Brush)FindResource(isWarning ? "WarningBorder" : "SuccessBorder");
        MessageText.Foreground = (Media.Brush)FindResource(isWarning ? "WarningText" : "SuccessText");
        MessageText.Text = message;
        MessageCard.Visibility = Wpf.Visibility.Visible;
    }

    private void HideMessage()
    {
        MessageCard.Visibility = Wpf.Visibility.Collapsed;
        MessageText.Text = string.Empty;
    }

    private void PopulateFriends(IReadOnlyList<NintendoFriend>? friends)
    {
        var items = new List<FriendListItem>();

        if (friends is not null)
        {
            items.AddRange(friends
                .OrderBy(friend => friend.Name ?? friend.NsaId, StringComparer.CurrentCultureIgnoreCase)
                .Select(friend => new FriendListItem(
                    friend.NsaId,
                    string.IsNullOrWhiteSpace(friend.Name) ? "Nintendo friend" : friend.Name!,
                    DescribePresence(friend.Presence),
                    friend.Presence?.PlayingTitle is not null)));
        }

        // Keep the saved selection visible even when it is not (or not yet) in the loaded friend list.
        if (_selectedNsaId is not null &&
            !items.Any(item => string.Equals(item.NsaId, _selectedNsaId, StringComparison.Ordinal)))
        {
            items.Insert(0, new FriendListItem(
                _selectedNsaId,
                string.IsNullOrWhiteSpace(_selectedName) ? "Saved account" : _selectedName!,
                friends is null ? "Saved selection" : "Saved selection · not in the bot's friend list",
                false));
        }

        _suppressSelectionChanged = true;
        try
        {
            FriendsListBox.ItemsSource = items;
            FriendsListBox.SelectedItem = items.FirstOrDefault(item =>
                string.Equals(item.NsaId, _selectedNsaId, StringComparison.Ordinal));
        }
        finally
        {
            _suppressSelectionChanged = false;
        }

        FriendsListBox.Visibility = items.Count > 0 ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;

        var hint = _friendsHint;
        FriendsHintText.Text = hint ?? string.Empty;
        FriendsHintText.Visibility = string.IsNullOrEmpty(hint) ? Wpf.Visibility.Collapsed : Wpf.Visibility.Visible;
    }

    private static string DescribePresence(NintendoPresence? presence)
    {
        if (presence?.PlayingTitle is { } title)
        {
            return $"Playing {title}";
        }

        return presence?.State is "ONLINE" or "PLAYING" ? "Online" : "Offline";
    }

    private void SelectInterval(int seconds)
    {
        var radio = seconds switch
        {
            >= 240 => Interval5Radio,
            >= 90 => Interval2Radio,
            _ => Interval1Radio
        };
        radio.IsChecked = true;
    }

    private int GetSelectedIntervalSeconds()
    {
        foreach (var radio in new[] { Interval1Radio, Interval2Radio, Interval5Radio })
        {
            if (radio.IsChecked == true && int.TryParse(radio.Tag as string, out var seconds))
            {
                return Math.Max(NintendoSettings.MinPollIntervalSeconds, seconds);
            }
        }

        return NintendoSettings.DefaultPollIntervalSeconds;
    }

    // ---------------------------------------------------------------- handlers

    private async void SignIn_Click(object sender, Wpf.RoutedEventArgs e)
    {
        HideMessage();

        var loginWindow = new NintendoLoginWindow(this);
        if (loginWindow.ShowDialog() != true || loginWindow.Account is null || _isClosed)
        {
            return;
        }

        _bot = loginWindow.Account;
        BotDetailOverride = null;
        UpdateBotUi();
        ShowMessage($"Signed in as {_bot.DisplayName}.", isWarning: false);
        _controller.Reconnect();

        await LoadFriendsAsync();
    }

    private async void SignOut_Click(object sender, Wpf.RoutedEventArgs e)
    {
        var confirmation = Wpf.MessageBox.Show(
            this,
            "Sign out the bot account? Your Switch game will stop showing in Steam until you sign in again.",
            "Nintendo Switch",
            Wpf.MessageBoxButton.YesNo,
            Wpf.MessageBoxImage.Question);
        if (confirmation != Wpf.MessageBoxResult.Yes)
        {
            return;
        }

        HideMessage();
        BeginBusy();
        try
        {
            var result = await NxapiBridge.LogoutAsync(_lifetime.Token);
            if (_isClosed)
            {
                return;
            }

            if (!result.Success)
            {
                ShowMessage(NintendoErrorText.Describe(result.ErrorCode, result.Message), isWarning: true);
                return;
            }

            _bot = null;
            _friends = null;
            _friendsHint = "Sign in the bot account to see its friends.";
            BotDetailOverride = null;
            PopulateFriends(null);
            ShowMessage("Signed out.", isWarning: false);
            _controller.Reconnect();
        }
        catch (OperationCanceledException)
        {
            // dialog closed
        }
        finally
        {
            EndBusy();
        }
    }

    private async void Refresh_Click(object sender, Wpf.RoutedEventArgs e)
    {
        HideMessage();
        await LoadFriendsAsync();
    }

    private void FriendsListBox_SelectionChanged(object sender, Controls.SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged || FriendsListBox.SelectedItem is not FriendListItem item)
        {
            return;
        }

        _selectedNsaId = item.NsaId;
        _selectedName = item.Name;
    }

    private void Save_Click(object sender, Wpf.RoutedEventArgs e)
    {
        var enabled = EnabledCheckBox.IsChecked == true;
        if (enabled && string.IsNullOrWhiteSpace(_selectedNsaId))
        {
            ShowMessage("Choose your Nintendo account from the list first.", isWarning: true);
            return;
        }

        var settings = _controller.Settings;
        settings.Enabled = enabled;
        settings.WatchedNsaId = _selectedNsaId;
        settings.WatchedName = _selectedNsaId is null ? null : _selectedName;
        settings.PollIntervalSeconds = GetSelectedIntervalSeconds();

        _controller.ApplySettings(settings);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, Wpf.RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Window_PreviewKeyDown(object sender, WpfInput.KeyEventArgs e)
    {
        if (e.Key == WpfInput.Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, WpfInput.MouseButtonEventArgs e)
    {
        if (e.ButtonState == WpfInput.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private sealed record FriendListItem(string NsaId, string Name, string PresenceText, bool IsPlaying);
}
