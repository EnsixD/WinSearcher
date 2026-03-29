using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WinSearcher.Models;
using WinSearcher.Services;

using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace WinSearcher;

public partial class MainWindow : Window
{
    private readonly SearchEngine _searchEngine;
    private readonly RecentAppsProvider _recentAppsProvider;
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _cts;
    private bool _hiding;

    public MainWindow()
    {
        InitializeComponent();

        _searchEngine       = new SearchEngine();
        _recentAppsProvider = new RecentAppsProvider();

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = RunSearchAsync(SearchBox.Text); };

        Loaded     += (_, _) => PositionWindow();
        Deactivated += (_, _) => { if (!_hiding) HideWindow(); };
    }

    // ── Focus fix: OnActivated fires after the window actually gets focus ───
    // This is more reliable than BeginInvoke because it fires from the OS event.
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    // ── Positioning ─────────────────────────────────────────────────────────

    private void PositionWindow()
    {
        var s = SystemParameters.WorkArea;
        Left = (s.Width - ActualWidth) / 2 + s.Left;
        Top  = s.Height * 0.15 + s.Top;
    }

    // ── Show / hide ─────────────────────────────────────────────────────────

    public void ShowAndActivate()
    {
        if (IsVisible && !_hiding) { HideWindow(); return; }   // toggle

        _hiding = false;
        PositionWindow();
        ResetUI();
        Show();
        Activate();   // triggers OnActivated → focus

        ((Storyboard)Resources["FadeInStoryboard"]).Begin(this);
        _ = LoadRecentAsync();
    }

    public void HideWindow()
    {
        if (_hiding) return;
        _hiding = true;

        _debounce.Stop();
        _cts?.Cancel();

        var sb = (Storyboard)Resources["FadeOutStoryboard"];
        EventHandler done = null!;
        done = (_, _) =>
        {
            sb.Completed -= done;
            Hide();
            _hiding = false;
        };
        sb.Completed += done;
        sb.Begin(this);
    }

    private void ResetUI()
    {
        SearchBox.Text           = string.Empty;
        ClearButton.Visibility   = Visibility.Collapsed;
        ResultsList.Visibility   = Visibility.Collapsed;
        EmptyState.Visibility    = Visibility.Collapsed;
        LoadingState.Visibility  = Visibility.Collapsed;
        Divider.Visibility       = Visibility.Collapsed;
        SectionHeader.Text       = "RECENT";
        SectionHeader.Visibility = Visibility.Collapsed;
    }

    // ── Recent apps ─────────────────────────────────────────────────────────

    private async Task LoadRecentAsync()
    {
        try
        {
            LoadingState.Visibility = Visibility.Visible;
            ResultsList.Visibility  = Visibility.Collapsed;

            var items = await _recentAppsProvider.GetRecentAppsAsync();

            LoadingState.Visibility = Visibility.Collapsed;

            if (items.Count > 0)
            {
                ResultsList.ItemsSource  = items;
                ResultsList.Visibility   = Visibility.Visible;
                Divider.Visibility       = Visibility.Visible;
                SectionHeader.Text       = "RECENT";
                SectionHeader.Visibility = Visibility.Visible;
                ResultsList.SelectedIndex = 0;
            }
        }
        catch { LoadingState.Visibility = Visibility.Collapsed; }
    }

    // ── Search ───────────────────────────────────────────────────────────────

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string text = SearchBox.Text;
        ClearButton.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

        if (string.IsNullOrWhiteSpace(text))
        {
            _debounce.Stop();
            SectionHeader.Text = "RECENT";
            _ = LoadRecentAsync();
            return;
        }

        SectionHeader.Text       = "RESULTS";
        SectionHeader.Visibility = Visibility.Visible;
        _debounce.Stop();
        _debounce.Start();
    }

    private async Task RunSearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            LoadingState.Visibility = Visibility.Visible;
            ResultsList.Visibility  = Visibility.Collapsed;
            EmptyState.Visibility   = Visibility.Collapsed;

            var results = await _searchEngine.SearchAsync(query, token);
            if (token.IsCancellationRequested) return;

            LoadingState.Visibility = Visibility.Collapsed;

            if (results.Count > 0)
            {
                ResultsList.ItemsSource   = results;
                ResultsList.Visibility    = Visibility.Visible;
                Divider.Visibility        = Visibility.Visible;
                ResultsList.SelectedIndex = 0;
            }
            else
            {
                Divider.Visibility     = Visibility.Visible;
                EmptyState.Visibility  = Visibility.Visible;
                EmptyStateSubtext.Text = $"Nothing found for \"{query}\"";
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            LoadingState.Visibility = Visibility.Collapsed;
            EmptyState.Visibility   = Visibility.Visible;
            EmptyStateSubtext.Text  = "Search error.";
        }
    }

    // ── Keyboard — single handler at window level ────────────────────────────

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                HideWindow();
                e.Handled = true;
                return;

            case Key.F4 when (Keyboard.Modifiers & ModifierKeys.Alt) != 0:
                HideWindow();
                e.Handled = true;
                return;

            case Key.Tab:
                // Keep focus on search box — don't let WPF cycle through controls
                SearchBox.Focus();
                e.Handled = true;
                return;

            case Key.Enter:
                OpenSelected();
                e.Handled = true;
                return;

            case Key.Down:
                MoveSelection(+1);
                e.Handled = true;
                return;

            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                return;
        }

        // Redirect printable keys to the search box when list has focus
        if (e.OriginalSource != SearchBox && IsTypingKey(e.Key))
            SearchBox.Focus();   // character will reach TextBox naturally

        base.OnPreviewKeyDown(e);
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e) { /* handled above */ }

    private void ResultsList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsTypingKey(e.Key) || e.Key == Key.Back)
            SearchBox.Focus();
    }

    private static bool IsTypingKey(Key k) =>
        (k >= Key.A && k <= Key.Z)          ||
        (k >= Key.D0 && k <= Key.D9)        ||
        (k >= Key.NumPad0 && k <= Key.NumPad9) ||
        k is Key.Space or Key.Back or Key.OemPeriod or Key.OemMinus;

    // ── Mouse ────────────────────────────────────────────────────────────────

    private void SearchBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => SearchBox.Focus();

    private void ResultsList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe && fe.DataContext is SearchResult)
            OpenSelected();
    }

    private void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem != null)
            ResultsList.ScrollIntoView(ResultsList.SelectedItem);
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void MoveSelection(int delta)
    {
        int count = ResultsList.Items.Count;
        if (count == 0) return;

        int next = ResultsList.SelectedIndex + delta;
        if (next < 0) next = count - 1;
        if (next >= count) next = 0;

        ResultsList.SelectedIndex = next;
        ResultsList.ScrollIntoView(ResultsList.SelectedItem);
        SearchBox.Focus();   // stay in TextBox so user can keep typing
    }

    private void OpenSelected()
    {
        if (ResultsList.SelectedItem is not SearchResult r) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = r.FullPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Open failed: {r.FullPath} — {ex.Message}");
        }
        HideWindow();
    }
}
