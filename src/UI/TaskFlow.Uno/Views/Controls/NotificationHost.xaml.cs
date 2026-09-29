using EF.UI.Client.Notifications;

namespace TaskFlow.Uno.Views.Controls;

/// <summary>Shows the notification service's items and dismisses them through the service when the user closes one.</summary>
public sealed partial class NotificationHost : UserControl
{
    public static readonly DependencyProperty NotificationsProperty = DependencyProperty.Register(
        nameof(Notifications),
        typeof(INotificationService),
        typeof(NotificationHost),
        new PropertyMetadata(null));

    /// <summary>Initializes notification host with required dependencies and default state.</summary>
    public NotificationHost()
    {
        this.InitializeComponent();
    }

    /// <summary>The service whose read-only <see cref="INotificationService.Items"/> this control displays.</summary>
    public INotificationService? Notifications
    {
        get => (INotificationService?)GetValue(NotificationsProperty);
        set => SetValue(NotificationsProperty, value);
    }

    /// <summary>Handles info bar closed events for notification host.</summary>
    private void OnInfoBarClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        // Only react to user-initiated closes - a programmatic dismiss by the notification service
        // removes the item itself, which would re-enter this handler with Reason=Programmatic otherwise.
        if (args.Reason != InfoBarCloseReason.CloseButton) return;

        if (sender.Tag is Guid id)
            Notifications?.Dismiss(id);
    }
}
