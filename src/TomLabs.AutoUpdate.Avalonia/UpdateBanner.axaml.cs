using Avalonia.Controls;

namespace TomLabs.AutoUpdate.Avalonia;

/// <summary>
/// Drop-in banner bound to <see cref="Updater.Current"/>. Hidden until an update is available, downloading,
/// ready or failed. Set the DataContext to a custom <see cref="UpdateBannerViewModel"/>
/// to bind a different updater.
/// </summary>
public partial class UpdateBanner : UserControl
{
    public UpdateBanner()
    {
        InitializeComponent();
        DataContext ??= new UpdateBannerViewModel();
    }

    protected override void OnAttachedToVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // The updater is usually started after the window is constructed; bind late if we were created too early.
        if (DataContext is UpdateBannerViewModel { State: UpdateState.Disabled } && Updater.Current != null)
            DataContext = new UpdateBannerViewModel(Updater.Current);
    }
}
