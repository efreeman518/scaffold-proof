using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Uno.Presentation.Presentation;

namespace TaskFlow.Uno.Views;

/// <summary>Hosts the task item XAML view and initializes its Uno page or control.</summary>
public sealed partial class TaskItemPage : Page
{
    /// <summary>Initializes task item page with required dependencies and default state.</summary>
    public TaskItemPage()
    {
        this.InitializeComponent();

        // Reset form state every time this page becomes visible. The
        // Visibility navigator reuses TaskItemPageModel, so state fields
        // retain their last values unless explicitly reset. Reset()
        // re-initializes all form state from Entity (empty for create,
        // entity values for edit).
        DataContextChanged += (_, _) => EnsureBindableDataContext();
        Loaded += (_, _) => PrepareForm();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (Visibility == Visibility.Visible) PrepareForm();
        });
    }

    private void PrepareForm()
    {
        EnsureBindableDataContext();
        TaskFormScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
        RequestReset();
    }

    private void EnsureBindableDataContext()
    {
        // Uno Navigation 7.3 can assign the raw model to a data-bearing route on Android,
        // which makes XAML render IState<T>.ToString(). Remove this boundary adapter when
        // the navigation mapping consistently supplies the generated proxy on all targets.
        if (DataContext is TaskItemPageModel model)
        {
            DataContext = new TaskItemPageBindableViewModel(model);
        }
    }

    /// <summary>Provides the request reset operation for task item page.</summary>
    private static void RequestReset() =>
        App.Host?.Services.GetService<IMessenger>()?.Send(new TaskFormResetMessage());
}
