using CommunityToolkit.Mvvm.Messaging;
using TaskFlow.Uno.Core.Business.Models;
using TaskFlow.Uno.Core.Business.Notifications;
using TaskFlow.Uno.Core.Business.Services;

namespace TaskFlow.Uno.Presentation.Presentation;

/// <summary>
/// MVUX state model for the task editor. It buffers child comments and checklist items in
/// create mode, persists children through the parent TaskItem payload, and maintains a
/// baseline snapshot for dirty-navigation checks.
/// </summary>
public partial record TaskItemPageModel
{
    public TaskItemModel? Entity { get; }
    private INavigator Navigator { get; }
    private ITaskItemApiService TaskItemService { get; }
    private ICategoryApiService CategoryService { get; }
    private ITagApiService TagService { get; }
    private IAttachmentApiService AttachmentService { get; }
    private IMessenger Messenger { get; }
    private IFormGuard FormGuard { get; }

    // Mutable baseline so post-save equality reflects server-echoed state
    // without requiring us to reassign the init-only Entity property.
    private TaskItemModel _baseline;

    /// <summary>Initializes task item page model with required dependencies and default state.</summary>
    public TaskItemPageModel(
        TaskItemModel? entity,
        INavigator navigator,
        ITaskItemApiService taskItemService,
        ICategoryApiService categoryService,
        ITagApiService tagService,
        IAttachmentApiService attachmentService,
        IMessenger messenger,
        IFormGuard formGuard)
    {
        Entity = entity;
        Navigator = navigator;
        TaskItemService = taskItemService;
        CategoryService = categoryService;
        TagService = tagService;
        AttachmentService = attachmentService;
        Messenger = messenger;
        FormGuard = formGuard;

        _baseline = entity ?? new TaskItemModel();
        FormGuard.IsDirtyAsync = ComputeIsDirtyAsync;

        Messenger.Register<TaskItemPageModel, TaskFormResetMessage>(this, static (recipient, msg) =>
        {
            _ = recipient.Reset().AsTask();
        });
    }

    public IState<bool> IsEditMode => State<bool>.Value(this, () => Entity?.Id is not null);

    // -- Form fields ----------------------------------------------
    public IState<string> Title => State<string>.Value(this, () => Entity?.Title ?? string.Empty);
    public IState<string> Description => State<string>.Value(this, () => Entity?.Description ?? string.Empty);
    public IState<string> Priority => State<string>.Value(this, () => Entity?.Priority ?? "None");
    public IState<string> Status => State<string>.Value(this, () => Entity?.Status ?? "Open");
    public IState<DateTimeOffset?> StartDate => State<DateTimeOffset?>.Value(this, () => Entity?.StartDate);
    public IState<DateTimeOffset?> DueDate => State<DateTimeOffset?>.Value(this, () => Entity?.DueDate);
    public IState<Guid?> SelectedCategoryId => State<Guid?>.Value(this, () => Entity?.CategoryId);

    // -- Dynamic header text --------------------------------------
    public IState<string> FormHeader => State<string>.Value(this, () => Entity?.Id is not null ? "Edit Task" : "New Task");
    public IState<string> FormSubheader => State<string>.Value(this, () => Entity?.Id is not null ? "Update details, manage checklist and comments" : "Fill in the details to create a new task");
    public IState<string> SaveButtonText => State<string>.Value(this, () => Entity?.Id is not null ? "Update Task" : "Save Task");

    // -- Lookup lists ---------------------------------------------
    public IListFeed<CategoryModel> Categories => ListFeed.Async(async ct =>
        (IImmutableList<CategoryModel>)(await CategoryService.SearchAsync(isActive: true, ct: ct)).ToImmutableList());

    public IListFeed<TagModel> AvailableTags => ListFeed.Async(async ct =>
        (IImmutableList<TagModel>)(await TagService.SearchAsync(ct: ct)).ToImmutableList());

    // -- Children (mutable lists - add/delete updates immediately; initial load fetches the
    //    TaskItem once since comments/checklist items arrive attached to it, not through a
    //    standalone route). --
    public IListState<CommentModel> Comments => ListState.Async(this, async ct =>
    {
        if (Entity?.Id is not Guid id) return ImmutableList<CommentModel>.Empty;
        var task = await TaskItemService.GetAsync(id, ct);
        return (IImmutableList<CommentModel>)(task?.Comments ?? []).ToImmutableList();
    });

    public IListState<ChecklistItemModel> ChecklistItems => ListState.Async(this, async ct =>
    {
        if (Entity?.Id is not Guid id) return ImmutableList<ChecklistItemModel>.Empty;
        var task = await TaskItemService.GetAsync(id, ct);
        return (IImmutableList<ChecklistItemModel>)(task?.ChecklistItems ?? []).ToImmutableList();
    });

    public IListState<AttachmentModel> Attachments => ListState.Async(this, async ct =>
    {
        if (Entity?.Id is null) return ImmutableList<AttachmentModel>.Empty;
        var result = await AttachmentService.SearchAsync(Entity.Id.Value, "TaskItem", ct);
        return (IImmutableList<AttachmentModel>)result.ToImmutableList();
    });

    // -- Inline add form states -----------------------------------
    public IState<string> NewCommentBody => State<string>.Value(this, () => string.Empty);
    public IState<string> NewChecklistTitle => State<string>.Value(this, () => string.Empty);

    // -- Form reset (fired on page Visibility->Visible so create mode
    //    always starts empty, even when the ViewModel instance is reused
    //    by the Visibility navigator). --
    /// <summary>
    /// Rebuilds field and child-list state from the current entity. Create mode clears child
    /// lists; edit mode reloads them from the API so stale local edits do not survive navigation.
    /// </summary>
    public async ValueTask Reset(CancellationToken ct = default)
    {
        var noCt = CancellationToken.None;
        await Title.UpdateAsync(_ => Entity?.Title ?? string.Empty, noCt);
        await Description.UpdateAsync(_ => Entity?.Description ?? string.Empty, noCt);
        await Priority.UpdateAsync(_ => Entity?.Priority ?? "None", noCt);
        await Status.UpdateAsync(_ => Entity?.Status ?? "Open", noCt);
        await StartDate.UpdateAsync(_ => Entity?.StartDate, noCt);
        await DueDate.UpdateAsync(_ => Entity?.DueDate, noCt);
        await SelectedCategoryId.UpdateAsync(_ => Entity?.CategoryId, noCt);
        await NewCommentBody.UpdateAsync(_ => string.Empty, noCt);
        await NewChecklistTitle.UpdateAsync(_ => string.Empty, noCt);

        // Reload children from server in edit mode (they arrive attached to the TaskItem itself -
        // comments/checklist items are never fetched through a standalone route); clear in create mode.
        if (Entity?.Id is Guid id)
        {
            var fresh = await TaskItemService.GetAsync(id, noCt) ?? Entity;
            await Comments.UpdateAsync(_ => (fresh.Comments ?? []).ToImmutableList(), noCt);
            await ChecklistItems.UpdateAsync(_ => (fresh.ChecklistItems ?? []).ToImmutableList(), noCt);
            var attachments = await AttachmentService.SearchAsync(id, "TaskItem", noCt);
            await Attachments.UpdateAsync(_ => attachments.ToImmutableList(), noCt);
            _baseline = fresh;
        }
        else
        {
            await Comments.UpdateAsync(_ => ImmutableList<CommentModel>.Empty, noCt);
            await ChecklistItems.UpdateAsync(_ => ImmutableList<ChecklistItemModel>.Empty, noCt);
            await Attachments.UpdateAsync(_ => ImmutableList<AttachmentModel>.Empty, noCt);
            _baseline = Entity ?? new TaskItemModel();
        }

        FormGuard.IsDirtyAsync = ComputeIsDirtyAsync;
    }

    /// <summary>
    /// Re-fetches the task after a child mutation: each one bumps the aggregate root's Version (D-031),
    /// which arrives only in the response ETag header - not exposed by this fetch-based client - so a
    /// reload is the reliable way to keep _baseline.Version (the next If-Match) current.
    /// </summary>
    private async ValueTask ReloadTaskAsync(CancellationToken ct)
    {
        if (Entity?.Id is not Guid id) return;
        var fresh = await TaskItemService.GetAsync(id, ct);
        if (fresh is null) return;
        _baseline = fresh;
        await Comments.UpdateAsync(_ => (fresh.Comments ?? []).ToImmutableList(), CancellationToken.None);
        await ChecklistItems.UpdateAsync(_ => (fresh.ChecklistItems ?? []).ToImmutableList(), CancellationToken.None);
    }

    // Called by the shell chrome before switching to a sibling route so
    // unsaved edits aren't silently discarded. Compares current field
    // state to the baseline snapshot taken on Reset/Save.
    private async ValueTask<bool> ComputeIsDirtyAsync(CancellationToken ct)
    {
        var title = (await Title) ?? string.Empty;
        var description = (await Description) ?? string.Empty;
        var priority = (await Priority) ?? "None";
        var status = (await Status) ?? "Open";
        var startDate = await StartDate;
        var dueDate = await DueDate;
        var categoryId = await SelectedCategoryId;
        var newComment = (await NewCommentBody) ?? string.Empty;
        var newChecklist = (await NewChecklistTitle) ?? string.Empty;

        var baseTitle = _baseline.Title ?? string.Empty;
        var baseDescription = _baseline.Description ?? string.Empty;
        var basePriority = _baseline.Priority ?? "None";
        var baseStatus = _baseline.Status ?? "Open";

        return title != baseTitle
            || description != baseDescription
            || priority != basePriority
            || status != baseStatus
            || startDate != _baseline.StartDate
            || dueDate != _baseline.DueDate
            || categoryId != _baseline.CategoryId
            || !string.IsNullOrWhiteSpace(newComment)
            || !string.IsNullOrWhiteSpace(newChecklist);
    }

    // -- Save (create or update) ----------------------------------
    /// <summary>
    /// Saves either a new aggregate or an existing aggregate. Children are never part of this
    /// payload - they mutate immediately through the aggregate root's dedicated nested routes
    /// (Add/Toggle/RemoveComment, Add/Toggle/RemoveChecklistItem below), never bundled into the
    /// whole-task PUT/POST.
    /// </summary>
    public async ValueTask Save(CancellationToken ct)
    {
        var title = await Title;
        var description = await Description;
        var priority = await Priority;
        var status = await Status;
        var startDate = await StartDate;
        var dueDate = await DueDate;
        var categoryId = await SelectedCategoryId;

        if (string.IsNullOrWhiteSpace(title)) return;

        var model = (Entity ?? new TaskItemModel()) with
        {
            Title = title,
            Description = description,
            Priority = priority ?? "None",
            Status = status ?? "Open",
            StartDate = startDate,
            DueDate = dueDate,
            CategoryId = categoryId
        };

        var wasCreate = !model.Id.HasValue;
        try
        {
            var saved = wasCreate
                ? await TaskItemService.CreateAsync(model, ct)
                : await TaskItemService.UpdateAsync(model, _baseline.Version, ct);

            _baseline = saved ?? model;
            FormGuard.Clear();

            Messenger.Send(new TaskItemsChangedMessage(ResetToFirstPage: wasCreate));

            if (wasCreate)
            {
                await Navigator.NavigateRouteAsync(this, "/Main/TaskList", cancellation: CancellationToken.None);
            }
            else
            {
                await Navigator.NavigateBackAsync(this, cancellation: CancellationToken.None);
            }
        }
        catch (ProblemDetailsException ex) when (ex.StatusCode == 412)
        {
            await ReloadTaskAsync(CancellationToken.None);
        }
    }

    // -- Delete ---------------------------------------------------
    public async ValueTask DeleteTask(CancellationToken ct)
    {
        if (Entity?.Id is null) return;
        try
        {
            await TaskItemService.DeleteAsync(Entity.Id.Value, _baseline.Version, ct);
            FormGuard.Clear();
            Messenger.Send(new TaskItemsChangedMessage(ResetToFirstPage: true));
            await Navigator.NavigateBackAsync(this, cancellation: CancellationToken.None);
        }
        catch (ProblemDetailsException ex) when (ex.StatusCode == 412)
        {
            await ReloadTaskAsync(CancellationToken.None);
        }
    }

    // -- Comment commands: mutate immediately through the aggregate root (GR-15) --
    public async ValueTask AddComment(CancellationToken ct)
    {
        var body = await NewCommentBody;
        if (string.IsNullOrWhiteSpace(body) || Entity?.Id is not Guid taskId) return;

        await TaskItemService.AddCommentAsync(taskId, body, ct);
        await NewCommentBody.UpdateAsync(_ => string.Empty, CancellationToken.None);
        await ReloadTaskAsync(CancellationToken.None);
    }

    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    public async ValueTask DeleteComment(CommentModel comment, CancellationToken ct)
    {
        if (Entity?.Id is not Guid taskId || comment.Id is not Guid commentId) return;
        try
        {
            await TaskItemService.RemoveCommentAsync(taskId, commentId, comment.Version, ct);
        }
        catch (ProblemDetailsException ex) when (ex.StatusCode == 412)
        {
            // Fall through to the reload below - the comment already changed elsewhere.
        }
        await ReloadTaskAsync(CancellationToken.None);
    }

    // -- Checklist commands: mutate immediately through the aggregate root (GR-15) --
    public async ValueTask AddChecklistItem(CancellationToken ct)
    {
        var title = await NewChecklistTitle;
        if (string.IsNullOrWhiteSpace(title) || Entity?.Id is not Guid taskId) return;

        var sortOrder = (await ChecklistItems)?.Count ?? 0;
        await TaskItemService.AddChecklistItemAsync(taskId, title, sortOrder, ct);
        await NewChecklistTitle.UpdateAsync(_ => string.Empty, CancellationToken.None);
        await ReloadTaskAsync(CancellationToken.None);
    }

    /// <summary>Converts the current value to ggle checklist item.</summary>
    public async ValueTask ToggleChecklistItem(ChecklistItemModel item, CancellationToken ct)
    {
        if (Entity?.Id is not Guid taskId) return;
        try
        {
            await TaskItemService.UpdateChecklistItemAsync(taskId, item with { IsCompleted = !item.IsCompleted }, item.Version, ct);
        }
        catch (ProblemDetailsException ex) when (ex.StatusCode == 412)
        {
            // Fall through to the reload below - the item already changed elsewhere.
        }
        await ReloadTaskAsync(CancellationToken.None);
    }

    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    public async ValueTask DeleteChecklistItem(ChecklistItemModel item, CancellationToken ct)
    {
        if (Entity?.Id is not Guid taskId || item.Id is not Guid itemId) return;
        try
        {
            await TaskItemService.RemoveChecklistItemAsync(taskId, itemId, item.Version, ct);
        }
        catch (ProblemDetailsException ex) when (ex.StatusCode == 412)
        {
            // Fall through to the reload below - the item already changed elsewhere.
        }
        await ReloadTaskAsync(CancellationToken.None);
    }

    // -- Attachment commands --------------------------------------
    public async ValueTask DeleteAttachment(AttachmentModel attachment, CancellationToken ct)
    {
        if (attachment.Id is null) return;
        await AttachmentService.DeleteAsync(attachment.Id.Value, attachment.Version, ct);
        await Attachments.UpdateAsync(list => (list ?? ImmutableList<AttachmentModel>.Empty).RemoveAll(a => a.Id == attachment.Id), ct);
    }
}
