namespace Test.Mobile.PageObjects;

/// <summary>The task editor (create + edit): form fields, checklist, comments, save and delete.</summary>
internal sealed class TaskItemScreen
{
    // Accessibility labels (content-desc) from src/UI/TaskFlow.Uno/Views/TaskItemPage.xaml.
    private const string TitleBoxId = "Task title";
    private const string DescriptionBoxId = "Task description";
    private const string PriorityComboId = "Task priority";
    private const string StatusComboId = "Task status";
    private const string SaveButtonId = "Save task";
    private const string DeleteButtonId = "Delete task";
    private const string ChecklistInputId = "New checklist item";
    private const string AddChecklistButtonId = "Add checklist item";
    private const string CommentInputId = "New comment";
    private const string AddCommentButtonId = "Add comment";

    private readonly MobileTaskAppDriver _app;

    public TaskItemScreen(MobileTaskAppDriver app) => _app = app;

    public void WaitUntilReady() => _app.Element(TitleBoxId);

    public void SetTitle(string title) => _app.Type(TitleBoxId, title);

    public void SetDescription(string description) => _app.Type(DescriptionBoxId, description);

    /// <summary>Best-effort priority pick; returns false if the native Spinner dropdown could not be driven.</summary>
    public bool TrySetPriority(string priority) =>
        _app.TrySelectFromCombo(PriorityComboId, priority, TimeSpan.FromSeconds(8));

    /// <summary>Best-effort status pick; returns false if the native Spinner dropdown could not be driven.</summary>
    public bool TrySetStatus(string status) =>
        _app.TrySelectFromCombo(StatusComboId, status, TimeSpan.FromSeconds(8));

    /// <summary>
    /// Best-effort: adds a checklist item via the inline add form (which sits below the fold and
    /// needs scrolling). Returns false if the field/button could not be driven on Skia. Never throws.
    /// </summary>
    public bool TryAddChecklistItem(string title)
    {
        if (!_app.TryType(ChecklistInputId, title, TimeSpan.FromSeconds(12))) return false;
        _app.HideKeyboard();
        if (!_app.TryTap(AddChecklistButtonId, TimeSpan.FromSeconds(8))) return false;
        return _app.HasText(title);
    }

    /// <summary>Best-effort: adds a comment via the inline add form. Returns false if not drivable. Never throws.</summary>
    public bool TryAddComment(string body)
    {
        if (!_app.TryType(CommentInputId, body, TimeSpan.FromSeconds(12))) return false;
        _app.HideKeyboard();
        if (!_app.TryTap(AddCommentButtonId, TimeSpan.FromSeconds(8))) return false;
        return _app.HasText(body);
    }

    /// <summary>Saves the task (create or update); the app navigates away on success.</summary>
    public void Save()
    {
        _app.HideKeyboard();
        _app.TapEnabled(SaveButtonId);
    }

    /// <summary>Deletes the task; only available in edit mode.</summary>
    public void Delete()
    {
        _app.HideKeyboard();
        _app.TapEnabled(DeleteButtonId);
    }

    public bool HasText(string text) => _app.HasText(text);

    /// <summary>
    /// Requires the Save control's accessibility label to be discoverable, including when Uno's
    /// long Skia form has preserved a scroll position that places the header outside the viewport.
    /// </summary>
    public void RequireAccessibleSave() => _app.Element(SaveButtonId);

    public void WaitForText(string text) => _app.WaitForText(text);
}
