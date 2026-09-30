using System.Windows;

namespace BaiduBatchTransfer.App.Services;

public interface IMessageBoxPresenter
{
    MessageBoxResult Show(string title, string message, MessageBoxButton buttons, MessageBoxImage image);
}

public sealed class WpfMessageBoxPresenter : IMessageBoxPresenter
{
    private readonly Func<Window?> ownerProvider;
    private readonly Func<Window?, string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult> show;

    public WpfMessageBoxPresenter() : this(
        () => System.Windows.Application.Current?.MainWindow,
        (owner, title, message, buttons, image) => owner is null
            ? MessageBox.Show(message, title, buttons, image)
            : MessageBox.Show(owner, message, title, buttons, image)) { }

    internal WpfMessageBoxPresenter(Func<Window?> ownerProvider,
        Func<Window?, string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult> show)
    {
        this.ownerProvider = ownerProvider;
        this.show = show;
    }

    public MessageBoxResult Show(string title, string message, MessageBoxButton buttons, MessageBoxImage image) =>
        show(ownerProvider(), title, message, buttons, image);
}
