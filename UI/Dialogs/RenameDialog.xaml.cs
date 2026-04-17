using System.Windows;
using System.Windows.Controls;
using SteamGameCustomStatus.Suggestions;
using Threading = System.Windows.Threading;
using WpfInput = System.Windows.Input;

namespace SteamGameCustomStatus.UI.Dialogs;

public partial class RenameDialog : Window
{
    private static readonly TimeSpan SuggestionRefreshDelay = TimeSpan.FromMilliseconds(280);
    private const int MaxSuggestionCount = 8;

    private readonly GameNameSuggestionService _suggestionService;
    private readonly Threading.DispatcherTimer _suggestionRefreshTimer;
    private CancellationTokenSource? _suggestionRefreshCancellation;
    private IReadOnlyList<GameNameSuggestion> _activeSuggestions = Array.Empty<GameNameSuggestion>();
    private bool _suppressSuggestionRefresh;

    public string? ResultName { get; private set; }

    internal RenameDialog(string currentName, Window? owner, GameNameSuggestionService suggestionService)
    {
        InitializeComponent();

        _suggestionService = suggestionService;
        _suggestionRefreshTimer = new Threading.DispatcherTimer
        {
            Interval = SuggestionRefreshDelay
        };
        _suggestionRefreshTimer.Tick += SuggestionRefreshTimer_Tick;

        _suppressSuggestionRefresh = true;
        NameTextBox.Text = currentName;
        NameTextBox.SelectAll();
        _suppressSuggestionRefresh = false;
        UpdateSuggestionStatus("Type for suggestions. Any custom name works.");

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

        Closed += RenameDialog_Closed;
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

    private void NameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSuggestionRefresh)
        {
            return;
        }

        QueueSuggestionRefresh();
    }

    private void NameTextBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateInlineSuggestion();
    }

    private void NameTextBox_KeyDown(object sender, WpfInput.KeyEventArgs e)
    {
        if (e.Key == WpfInput.Key.Down)
        {
            MoveSuggestionSelection(1);
            e.Handled = _activeSuggestions.Count > 0;
            return;
        }

        if (e.Key == WpfInput.Key.Up)
        {
            MoveSuggestionSelection(-1);
            e.Handled = _activeSuggestions.Count > 0;
            return;
        }

        if (e.Key == WpfInput.Key.Tab && TryApplyHighlightedSuggestion(preferTopSuggestionWhenNothingSelected: true))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == WpfInput.Key.Enter)
        {
            if (SuggestionsPopup.IsOpen && TryApplyHighlightedSuggestion(preferTopSuggestionWhenNothingSelected: true))
            {
                e.Handled = true;
                return;
            }

            Accept();
            e.Handled = true;
        }
        else if (e.Key == WpfInput.Key.Escape)
        {
            if (SuggestionsPopup.IsOpen)
            {
                ClearSuggestions();
                UpdateSuggestionStatus("Suggestions hidden. Any name works.");
                e.Handled = true;
                return;
            }

            ResultName = null;
            DialogResult = false;
            e.Handled = true;
        }
    }

    private void SuggestionsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateInlineSuggestion();
    }

    private void SuggestionsListBox_PreviewMouseLeftButtonUp(object sender, WpfInput.MouseButtonEventArgs e)
    {
        if (SuggestionsListBox.SelectedItem is not GameNameSuggestion selectedSuggestion)
        {
            return;
        }

        ApplySuggestion(selectedSuggestion);
        NameTextBox.Focus();
        e.Handled = true;
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

    private void RenameDialog_Closed(object? sender, EventArgs e)
    {
        _suggestionRefreshTimer.Stop();
        _suggestionRefreshTimer.Tick -= SuggestionRefreshTimer_Tick;
        CancelPendingSuggestionRefresh();
    }

    private void QueueSuggestionRefresh()
    {
        _suggestionRefreshTimer.Stop();
        CancelPendingSuggestionRefresh();

        var query = NameTextBox.Text.Trim();
        if (query.Length < 2)
        {
            ClearSuggestions();
            UpdateSuggestionStatus(string.IsNullOrWhiteSpace(query)
                ? "Type for game suggestions."
                : "Type 2+ characters.");
            return;
        }

        UpdateSuggestionStatus("Searching suggestions...");
        _suggestionRefreshTimer.Start();
    }

    private async void SuggestionRefreshTimer_Tick(object? sender, EventArgs e)
    {
        _suggestionRefreshTimer.Stop();
        await RefreshSuggestionsAsync();
    }

    private async Task RefreshSuggestionsAsync()
    {
        CancelPendingSuggestionRefresh();

        var query = NameTextBox.Text.Trim();
        if (query.Length < 2)
        {
            ClearSuggestions();
            UpdateSuggestionStatus("Type 2+ characters.");
            return;
        }

        var cancellation = new CancellationTokenSource();
        _suggestionRefreshCancellation = cancellation;

        IReadOnlyList<GameNameSuggestion> suggestions;
        try
        {
            var offlineSuggestions = await _suggestionService.GetOfflineSuggestionsAsync(query, MaxSuggestionCount, cancellation.Token);
            if (!ReferenceEquals(_suggestionRefreshCancellation, cancellation) || !IsLoaded)
            {
                return;
            }

            if (offlineSuggestions.Count > 0)
            {
                SetSuggestions(offlineSuggestions);
                UpdateSuggestionStatus("Searching online suggestions...");
            }

            suggestions = await _suggestionService.GetSuggestionsAsync(query, MaxSuggestionCount, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            suggestions = Array.Empty<GameNameSuggestion>();
        }

        if (!ReferenceEquals(_suggestionRefreshCancellation, cancellation) || !IsLoaded)
        {
            return;
        }

        SetSuggestions(suggestions);
    }

    private void SetSuggestions(IReadOnlyList<GameNameSuggestion> suggestions)
    {
        _activeSuggestions = suggestions;
        SuggestionsListBox.ItemsSource = suggestions;
        SuggestionsListBox.SelectedIndex = -1;
        SuggestionsPopup.IsOpen = suggestions.Count > 0;
        UpdateInlineSuggestion();

        if (suggestions.Count == 0)
        {
            UpdateSuggestionStatus("No matches. Any name works.");
            return;
        }

        UpdateSuggestionStatus(
            suggestions.Count == 1
                ? "1 match. Tab fills, Enter picks."
                : $"{suggestions.Count} matches. Tab fills, Enter picks.");
    }

    private void ClearSuggestions()
    {
        _activeSuggestions = Array.Empty<GameNameSuggestion>();
        SuggestionsListBox.ItemsSource = null;
        SuggestionsListBox.SelectedIndex = -1;
        SuggestionsPopup.IsOpen = false;
        ClearInlineSuggestion();
    }

    private void CancelPendingSuggestionRefresh()
    {
        if (_suggestionRefreshCancellation is null)
        {
            return;
        }

        _suggestionRefreshCancellation.Cancel();
        _suggestionRefreshCancellation.Dispose();
        _suggestionRefreshCancellation = null;
    }

    private void MoveSuggestionSelection(int delta)
    {
        if (_activeSuggestions.Count == 0)
        {
            return;
        }

        if (!SuggestionsPopup.IsOpen)
        {
            SuggestionsPopup.IsOpen = true;
        }

        var nextIndex = SuggestionsListBox.SelectedIndex;
        if (nextIndex < 0)
        {
            nextIndex = delta > 0 ? 0 : _activeSuggestions.Count - 1;
        }
        else
        {
            nextIndex = delta switch
            {
                < 0 when nextIndex == 0 => _activeSuggestions.Count - 1,
                > 0 when nextIndex == _activeSuggestions.Count - 1 => 0,
                _ => Math.Clamp(nextIndex + delta, 0, _activeSuggestions.Count - 1)
            };
        }

        SuggestionsListBox.SelectedIndex = nextIndex;
        SuggestionsListBox.ScrollIntoView(SuggestionsListBox.SelectedItem);
        UpdateInlineSuggestion();
    }

    private bool TryApplyHighlightedSuggestion(bool preferTopSuggestionWhenNothingSelected)
    {
        var suggestion = GetHighlightedSuggestion(preferTopSuggestionWhenNothingSelected);
        if (suggestion is null)
        {
            return false;
        }

        if (string.Equals(NameTextBox.Text.Trim(), suggestion.Title, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ApplySuggestion(suggestion);
        return true;
    }

    private GameNameSuggestion? GetHighlightedSuggestion(bool preferTopSuggestionWhenNothingSelected)
    {
        if (SuggestionsListBox.SelectedItem is GameNameSuggestion selectedSuggestion)
        {
            return selectedSuggestion;
        }

        if (preferTopSuggestionWhenNothingSelected && _activeSuggestions.Count > 0)
        {
            return _activeSuggestions[0];
        }

        return null;
    }

    private void ApplySuggestion(GameNameSuggestion suggestion)
    {
        _suppressSuggestionRefresh = true;
        NameTextBox.Text = suggestion.Title;
        NameTextBox.CaretIndex = suggestion.Title.Length;
        NameTextBox.Select(NameTextBox.Text.Length, 0);
        _suppressSuggestionRefresh = false;

        ClearSuggestions();
        UpdateSuggestionStatus("Selected. Press Enter.");
        UpdateInlineSuggestion();
    }

    private void UpdateInlineSuggestion()
    {
        var currentText = NameTextBox.Text;
        if (string.IsNullOrEmpty(currentText) || NameTextBox.SelectionLength > 0 || NameTextBox.CaretIndex != currentText.Length)
        {
            ClearInlineSuggestion();
            return;
        }

        var suggestion = GetHighlightedSuggestion(preferTopSuggestionWhenNothingSelected: true);
        if (suggestion is null ||
            suggestion.Title.Length <= currentText.Length ||
            !suggestion.Title.StartsWith(currentText, StringComparison.CurrentCultureIgnoreCase))
        {
            ClearInlineSuggestion();
            return;
        }

        InlineSuggestionPrefixRun.Text = currentText;
        InlineSuggestionSuffixRun.Text = suggestion.Title[currentText.Length..];
        InlineSuggestionText.Visibility = Visibility.Visible;
    }

    private void ClearInlineSuggestion()
    {
        InlineSuggestionPrefixRun.Text = string.Empty;
        InlineSuggestionSuffixRun.Text = string.Empty;
        InlineSuggestionText.Visibility = Visibility.Collapsed;
    }

    private void UpdateSuggestionStatus(string message)
    {
        SuggestionStatusText.Text = message;
    }
}





