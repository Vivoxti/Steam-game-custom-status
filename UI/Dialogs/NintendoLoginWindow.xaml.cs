using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using SteamGameCustomStatus.Infrastructure;
using SteamGameCustomStatus.Nintendo;
using Wpf = System.Windows;
using WpfInput = System.Windows.Input;
using WpfInterop = System.Windows.Interop;

namespace SteamGameCustomStatus.UI.Dialogs;

public partial class NintendoLoginWindow
{
    // Nintendo's "Select this account" button points at this custom scheme; the fragment carries the auth code.
    private const string RedirectPrefix = "npf71b963c1b7b6d119://auth";

    private const string BrowserModeLinkText = "Sign in with your browser instead";
    private const string BuiltInModeLinkText = "Use the built-in browser instead";

    private readonly CancellationTokenSource _lifetime = new();

    private AuthUrlInfo? _auth;
    private bool _webViewReady;
    private bool _webViewFailed;
    private string? _webViewFailureReason;
    private bool _fallbackMode;
    private bool _needsRetry;
    private bool _isLoggingIn;
    private bool _isClosed;
    private string? _busyText;

    /// <summary>
    /// The bot account after a successful sign-in (when the dialog result is true).
    /// </summary>
    internal NintendoAccount? Account { get; private set; }

    internal NintendoLoginWindow(Wpf.Window? owner)
    {
        InitializeComponent();

        Height = Math.Min(Height, Math.Max(480, Wpf.SystemParameters.WorkArea.Height - 40));

        if (owner is { IsVisible: true, WindowState: not Wpf.WindowState.Minimized })
        {
            Owner = owner;
            WindowStartupLocation = Wpf.WindowStartupLocation.CenterOwner;
        }

        Loaded += NintendoLoginWindow_Loaded;
        Closed += NintendoLoginWindow_Closed;
        UpdateView();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TryRoundCorners();
    }

    private async void NintendoLoginWindow_Loaded(object sender, Wpf.RoutedEventArgs e)
    {
        await StartAsync(openInBrowser: false);
    }

    private void NintendoLoginWindow_Closed(object? sender, EventArgs e)
    {
        _isClosed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();

        try
        {
            Browser.Dispose();
        }
        catch
        {
            // ignored
        }
    }

    // ---------------------------------------------------------------- sign-in flow

    /// <summary>
    /// Fetches a fresh auth URL (new verifier) and shows it in the embedded browser, or, in browser mode,
    /// optionally opens it in the default browser.
    /// </summary>
    private async Task StartAsync(bool openInBrowser)
    {
        _needsRetry = false;
        HideError();
        SetBusy("Preparing sign-in…");

        try
        {
            var result = await NxapiBridge.GetAuthUrlAsync(_lifetime.Token);
            if (_isClosed)
            {
                return;
            }

            if (!result.Success || result.Value is null || string.IsNullOrWhiteSpace(result.Value.Url))
            {
                _auth = null;
                ShowError(NintendoErrorText.Describe(result.ErrorCode, result.Message), allowRetry: true);
                return;
            }

            _auth = result.Value;

            if (!_fallbackMode)
            {
                await EnsureWebViewAsync();
                if (_isClosed)
                {
                    return;
                }
            }

            if (_fallbackMode)
            {
                if (openInBrowser)
                {
                    OpenInBrowser();
                }
            }
            else
            {
                Browser.CoreWebView2.Navigate(_auth.Url);
            }
        }
        catch (OperationCanceledException)
        {
            // window closed
        }
        finally
        {
            if (!_isClosed)
            {
                SetBusy(null);
            }
        }
    }

    private async Task EnsureWebViewAsync()
    {
        if (_webViewReady || _webViewFailed)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(AppDataLocation.WebViewDataDirectory);
            var environment = await CoreWebView2Environment.CreateAsync(null, AppDataLocation.WebViewDataDirectory);
            await Browser.EnsureCoreWebView2Async(environment);

            var core = Browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NavigationStarting += CoreWebView_NavigationStarting;
            core.LaunchingExternalUriScheme += CoreWebView_LaunchingExternalUriScheme;

            _webViewReady = true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _webViewFailed = true;
            _fallbackMode = true;
            _webViewFailureReason = exception is WebView2RuntimeNotFoundException
                ? "The Microsoft Edge WebView2 runtime is not installed, so the built-in browser is unavailable."
                : $"The built-in browser could not start ({exception.Message}).";
        }
    }

    private void CoreWebView_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsRedirectUri(e.Uri))
        {
            e.Cancel = true;
            BeginLogin(e.Uri);
        }
    }

    private void CoreWebView_LaunchingExternalUriScheme(object? sender, CoreWebView2LaunchingExternalUriSchemeEventArgs e)
    {
        if (IsRedirectUri(e.Uri))
        {
            e.Cancel = true;
            BeginLogin(e.Uri);
        }
    }

    private static bool IsRedirectUri(string? uri)
    {
        return !string.IsNullOrWhiteSpace(uri) &&
               uri.StartsWith(RedirectPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private async void BeginLogin(string redirectUri)
    {
        if (_isLoggingIn || _isClosed || _auth is null)
        {
            return;
        }

        _isLoggingIn = true;
        HideError();
        SetBusy("Signing in…");

        try
        {
            var result = await NxapiBridge.LoginAsync(_auth.Verifier, redirectUri, _lifetime.Token);
            if (_isClosed)
            {
                return;
            }

            if (result.Success && result.Value is not null)
            {
                Account = result.Value;
                DialogResult = true;
                return;
            }

            // The verifier is single-use, so a retry always fetches a fresh auth URL.
            ShowError(NintendoErrorText.Describe(result.ErrorCode, result.Message), allowRetry: true);
        }
        catch (OperationCanceledException)
        {
            // window closed
        }
        finally
        {
            _isLoggingIn = false;
            if (!_isClosed)
            {
                SetBusy(null);
            }
        }
    }

    private void OpenInBrowser()
    {
        if (_auth is null)
        {
            return;
        }

        try
        {
            if (!Uri.TryCreate(_auth.Url, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("https" or "http"))
            {
                ShowError("The sign-in address was not valid. Try again.", allowRetry: true);
                return;
            }

            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ShowError($"Could not open your browser: {exception.Message}", allowRetry: false);
        }
    }

    // ---------------------------------------------------------------- view state

    private void SetBusy(string? text)
    {
        _busyText = text;
        UpdateView();
    }

    private void ShowError(string message, bool allowRetry)
    {
        ErrorText.Text = message;
        RetryButton.Visibility = allowRetry ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        ErrorCard.Visibility = Wpf.Visibility.Visible;
        _needsRetry = allowRetry;
        UpdateView();
    }

    private void HideError()
    {
        ErrorCard.Visibility = Wpf.Visibility.Collapsed;
        ErrorText.Text = string.Empty;
    }

    private void UpdateView()
    {
        var isBusy = _busyText is not null;
        BusyText.Text = _busyText ?? string.Empty;
        BusyPanel.Visibility = isBusy ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;

        // WebView2 is HWND-hosted, so it must be hidden (not just covered) while another panel is shown.
        // Hidden rather than Collapsed so the control is still laid out and its HWND exists for initialization.
        var showContent = !isBusy && !_needsRetry;
        Browser.Visibility = showContent && !_fallbackMode && _webViewReady
            ? Wpf.Visibility.Visible
            : Wpf.Visibility.Hidden;
        FallbackPanel.Visibility = showContent && _fallbackMode
            ? Wpf.Visibility.Visible
            : Wpf.Visibility.Collapsed;

        FallbackReasonText.Text = _webViewFailureReason ?? string.Empty;
        FallbackReasonText.Visibility = _webViewFailed ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;

        ModeLinkButton.Content = _fallbackMode ? BuiltInModeLinkText : BrowserModeLinkText;
        ModeLinkButton.Visibility = _fallbackMode && _webViewFailed ? Wpf.Visibility.Collapsed : Wpf.Visibility.Visible;
        ModeLinkButton.IsEnabled = !isBusy;
    }

    // ---------------------------------------------------------------- handlers

    private async void Retry_Click(object sender, Wpf.RoutedEventArgs e)
    {
        await StartAsync(openInBrowser: _fallbackMode);
    }

    private async void ModeLink_Click(object sender, Wpf.RoutedEventArgs e)
    {
        HideError();
        _needsRetry = false;

        if (_fallbackMode)
        {
            // Back to the built-in browser (same auth URL, same verifier).
            _fallbackMode = false;
            if (_auth is null)
            {
                await StartAsync(openInBrowser: false);
                return;
            }

            SetBusy("Preparing sign-in…");
            try
            {
                await EnsureWebViewAsync();
                if (_isClosed)
                {
                    return;
                }

                if (!_fallbackMode)
                {
                    Browser.CoreWebView2.Navigate(_auth.Url);
                }
            }
            finally
            {
                if (!_isClosed)
                {
                    SetBusy(null);
                }
            }

            return;
        }

        _fallbackMode = true;
        if (_auth is null)
        {
            await StartAsync(openInBrowser: true);
            return;
        }

        UpdateView();
        OpenInBrowser();
    }

    private void OpenBrowser_Click(object sender, Wpf.RoutedEventArgs e)
    {
        OpenInBrowser();
    }

    private void Continue_Click(object sender, Wpf.RoutedEventArgs e)
    {
        SubmitPastedLink();
    }

    private void PasteTextBox_PreviewKeyDown(object sender, WpfInput.KeyEventArgs e)
    {
        if (e.Key == WpfInput.Key.Enter)
        {
            SubmitPastedLink();
            e.Handled = true;
        }
    }

    private void SubmitPastedLink()
    {
        var text = PasteTextBox.Text.Trim().Trim('"');
        if (!IsRedirectUri(text))
        {
            ShowError(
                $"That does not look like the right link. It should start with {RedirectPrefix}",
                allowRetry: false);
            return;
        }

        if (_auth is null)
        {
            ShowError("Open the sign-in page first, then paste the link.", allowRetry: false);
            return;
        }

        BeginLogin(text);
    }

    private void Close_Click(object sender, Wpf.RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Window_PreviewKeyDown(object sender, WpfInput.KeyEventArgs e)
    {
        if (e.Key == WpfInput.Key.Escape && !PasteTextBox.IsKeyboardFocusWithin)
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

    // ---------------------------------------------------------------- window chrome

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void TryRoundCorners()
    {
        try
        {
            var handle = new WpfInterop.WindowInteropHelper(this).Handle;
            var preference = DwmwcpRound;
            DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch
        {
            // best effort (pre-Windows 11)
        }
    }
}
