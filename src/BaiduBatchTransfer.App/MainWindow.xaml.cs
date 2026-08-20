using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
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
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
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
        liveRegionNotifier?.Dispose();
        liveRegionNotifier = null;
        DataContextChanged -= OnDataContextChanged;
        Closed -= OnClosed;
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
