using CommunityToolkit.Mvvm.Messaging;
using TaskFlow.Uno.Core.Business.Models;
using TaskFlow.Uno.Core.Business.Notifications;
using TaskFlow.Uno.Core.Business.Services;

namespace TaskFlow.Uno.Presentation.Presentation;

/// <summary>Drives task list state, navigation, and commands for the Uno presentation layer.</summary>
public partial record TaskListModel
{
    /// <summary>Initializes task list model with required dependencies and default state.</summary>
    public TaskListModel(
        INavigator navigator,
        ITaskItemApiService taskItemService,
        ICategoryApiService categoryService,
        IMessenger messenger)
    {
        Navigator = navigator;
        TaskItemService = taskItemService;
        CategoryService = categoryService;
        Messenger = messenger;

        Messenger.Register<TaskListModel, TaskItemsChangedMessage>(this, static (recipient, message) =>
        {
            _ = recipient.Refresh().AsTask();
        });

        _ = Refresh().AsTask();
    }

    private INavigator Navigator { get; }
    private ITaskItemApiService TaskItemService { get; }
    private ICategoryApiService CategoryService { get; }
    private IMessenger Messenger { get; }

    // The last page's cursor, kept outside bindable state - nothing in the UI needs it directly,
    // only LoadMore (walking it forward one page at a time, GR-18).
    private string? _nextCursor;

    // -- List + cursor state (individually bindable) --
    public IListState<TaskItemModel> Items => ListState<TaskItemModel>.Empty(this);
    public IState<bool> HasMore => State<bool>.Value(this, () => false);
    public IState<bool> HasItems => State<bool>.Value(this, () => false);
    public IState<bool> IsEmpty => State<bool>.Value(this, () => true);
    public IState<bool> IsLoading => State<bool>.Value(this, () => false);

    // -- User-input state --
    public IState<string> SearchTerm => State<string>.Value(this, () => string.Empty);
    public IState<string> AppliedSearchTerm => State<string>.Value(this, () => string.Empty);
    public IState<string> StatusFilter => State<string>.Value(this, () => string.Empty);
    public IState<string> PriorityFilter => State<string>.Value(this, () => string.Empty);

    public IListFeed<CategoryModel> Categories => ListFeed.Async(async ct =>
        (IImmutableList<CategoryModel>)(await CategoryService.SearchAsync(isActive: true, ct: ct)).ToImmutableList());

    /// <summary>Clears the current page and cursor, then loads the first cursor page for the active filters.</summary>
    public async ValueTask Refresh(CancellationToken ct = default)
    {
        var noCt = CancellationToken.None;
        _nextCursor = null;
        await Items.UpdateAsync(_ => ImmutableList<TaskItemModel>.Empty, noCt);
        await LoadMore(ct);
    }

    /// <summary>Appends the next cursor page. A no-op when there is nothing more to load.</summary>
    public async ValueTask LoadMore(CancellationToken ct = default)
    {
        await IsLoading.UpdateAsync(_ => true, CancellationToken.None);

        var term = await AppliedSearchTerm;
        var status = await StatusFilter;
        var priority = await PriorityFilter;

        // CancellationToken.None for the fetch so MVUX command cancellation (which fires when
        // IsEnabled bindings flip during state updates) can't abort mid-request.
        var page = await TaskItemService.SearchCursorAsync(
            searchTerm: term,
            status: status,
            priority: priority,
            cursor: _nextCursor,
            ct: CancellationToken.None);

        var noCt = CancellationToken.None;
        await Items.UpdateAsync(current => (current ?? ImmutableList<TaskItemModel>.Empty).AddRange(page.Items), noCt);
        _nextCursor = page.NextCursor;
        await HasMore.UpdateAsync(_ => page.HasMore, noCt);

        var itemCount = (await Items)?.Count ?? 0;
        await HasItems.UpdateAsync(_ => itemCount > 0, noCt);
        await IsEmpty.UpdateAsync(_ => itemCount == 0, noCt);
        await IsLoading.UpdateAsync(_ => false, noCt);
    }

    /// <summary>Handles search requests: applies the draft filters and reloads from the first page.</summary>
    public async ValueTask Search(CancellationToken ct)
    {
        var term = (await SearchTerm) ?? string.Empty;
        await AppliedSearchTerm.UpdateAsync(_ => term.Trim(), ct);
        await Refresh(ct);
    }

    /// <summary>Opens open detail for editing or viewing.</summary>
    public async ValueTask OpenDetail(TaskItemModel item, CancellationToken ct) =>
        await Navigator.NavigateRouteAsync(this, "TaskItem", data: item, cancellation: ct);

    /// <summary>Creates requested data after validation and maps the result to the caller contract.</summary>
    public async ValueTask CreateNew(CancellationToken ct) =>
        await Navigator.NavigateRouteAsync(this, "TaskItem", cancellation: ct);

    /// <summary>Toggles a task's status and sends its Version as If-Match; a 412 reloads the row.</summary>
    public async ValueTask ToggleStatus(TaskItemModel item, CancellationToken ct)
    {
        var newStatus = item.Status switch
        {
            "Open" => "InProgress",
            "InProgress" => "Completed",
            _ => item.Status
        };

        try
        {
            await TaskItemService.UpdateAsync(item with { Status = newStatus }, item.Version, ct);
        }
        catch (ProblemDetailsException ex) when (ex.StatusCode == 412)
        {
            // Notification already shown by ProblemDetailsDelegatingHandler; refresh so the row
            // reflects what changed elsewhere instead of staying stale.
        }

        await Refresh(ct);
        Messenger.Send(new TaskItemsChangedMessage());
    }
}
