using EF.UI.Client.Notifications;

namespace TaskFlow.Uno.Presentation.Presentation;

/// <summary>Drives main state, navigation, and commands for the Uno presentation layer.</summary>
public partial record MainModel
{
    /// <summary>Initializes main model with required dependencies and default state.</summary>
    public MainModel(IBusyTracker busy, INotificationService notifications)
    {
        Busy = busy;
        Notifications = notifications;
    }

    public IBusyTracker Busy { get; }
    public INotificationService Notifications { get; }

    /// <summary>Dismisses the notification with the given id.</summary>
    public void Dismiss(Guid id) => Notifications.Dismiss(id);
}
