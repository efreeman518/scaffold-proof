using TaskFlow.Uno.Presentation.Presentation;

namespace TaskFlow.Uno.Views;

/// <summary>Adapts a raw navigation model to its generated MVUX binding proxy.</summary>
internal sealed class TaskItemPageBindableViewModel(TaskItemPageModel model) : TaskItemPageViewModel(model);
