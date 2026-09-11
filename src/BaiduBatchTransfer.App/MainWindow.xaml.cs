using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace BaiduBatchTransfer.App;

public partial class MainWindow : Window
{
    private PropertyChangeLiveRegionNotifier? liveRegionNotifier;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
        StateChanged += OnWindowStateChanged;
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) =>
        SystemCommands.MinimizeWindow(this);

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e) =>
        ToggleMaximizeRestore();

    private void OnCloseClick(object sender, RoutedEventArgs e) =>
        SystemCommands.CloseWindow(this);

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsInsideButton(source)) return;

        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            e.Handled = true;
            return;
        }

        DragMove();
    }

    private void ToggleMaximizeRestore()
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) =>
        MaximizeRestoreButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";

    private static bool IsInsideButton(DependencyObject source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Button) return true;
        }

        return false;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        liveRegionNotifier?.Dispose();
        liveRegionNotifier = e.NewValue is MainWindowViewModel viewModel
            ? new PropertyChangeLiveRegionNotifier(
                viewModel,
                new Dictionary<string, Action>
                {
                    [nameof(MainWindowViewModel.AccountStatusMessage)] =
                        () => RaiseLiveRegionChanged(AccountStatusLiveRegion),
                    [nameof(MainWindowViewModel.StatusMessage)] =
                        () => RaiseLiveRegionChanged(OperationStatusLiveRegion),
                },
                DispatchAnnouncement)
            : null;
    }

    private void DispatchAnnouncement(Action announcement)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(announcement, DispatcherPriority.ContextIdle);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel) viewModel.Close();
        liveRegionNotifier?.Dispose();
        liveRegionNotifier = null;
        DataContextChanged -= OnDataContextChanged;
        Closed -= OnClosed;
        StateChanged -= OnWindowStateChanged;
    }

    private static void RaiseLiveRegionChanged(FrameworkElement element)
    {
        var peer = UIElementAutomationPeer.FromElement(element) ??
                   UIElementAutomationPeer.CreatePeerForElement(element);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}

public sealed class PropertyChangeLiveRegionNotifier : IDisposable
{
    private readonly INotifyPropertyChanged source;
    private readonly IReadOnlyDictionary<string, Action> announcements;
    private readonly Action<Action> dispatch;
    private int disposed;

    public PropertyChangeLiveRegionNotifier(
        INotifyPropertyChanged source,
        IReadOnlyDictionary<string, Action> announcements,
        Action<Action> dispatch)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.announcements = announcements ?? throw new ArgumentNullException(nameof(announcements));
        this.dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        source.PropertyChanged += OnPropertyChanged;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        source.PropertyChanged -= OnPropertyChanged;
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Volatile.Read(ref disposed) != 0 ||
            e.PropertyName is null ||
            !announcements.TryGetValue(e.PropertyName, out var announce))
            return;

        dispatch(() =>
        {
            if (Volatile.Read(ref disposed) == 0) announce();
        });
    }
}
