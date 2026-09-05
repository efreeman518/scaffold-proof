using CommunityToolkit.Mvvm.Messaging;
using TaskFlow.Uno.Core.Business.Models;
using TaskFlow.Uno.Core.Business.Notifications;
using TaskFlow.Uno.Core.Business.Services;

namespace TaskFlow.Uno.Presentation.Presentation;

/// <summary>Drives category tree state, navigation, and commands for the Uno presentation layer.</summary>
public partial record CategoryTreeModel(
    INavigator Navigator,
    ICategoryApiService CategoryService,
    IMessenger Messenger)
{
    private CategoryModel? _editingCategory;

    public IState<int> CategoriesVersion => State<int>.Value(this, () => 0);
    public IState<bool> IsEditing => State<bool>.Value(this, () => false);
    public IState<bool> IsCreating => State<bool>.Value(this, () => true);

    public IListFeed<CategoryModel> Categories => ListFeed.Async(async ct =>
    {
        _ = await CategoriesVersion;
        return (IImmutableList<CategoryModel>)(await CategoryService.SearchAsync(ct: ct))
            .OrderBy(category => category.ParentCategoryId.HasValue ? 1 : 0)
            .ThenBy(category => category.SortOrder)
            .ThenBy(category => category.Name)
            .ToImmutableList();
    });

    public IState<string> NewCategoryName => State<string>.Value(this, () => string.Empty);
    public IState<string> NewCategoryDescription => State<string>.Value(this, () => string.Empty);
    public IState<Guid?> SelectedParentId => State<Guid?>.Value(this, () => null);

    /// <summary>Provides the save category operation for category tree model.</summary>
    public async ValueTask SaveCategory(CancellationToken ct)
    {
        var name = await NewCategoryName;
        var description = await NewCategoryDescription;
        var parentId = await SelectedParentId;
        var editing = _editingCategory;

        if (string.IsNullOrWhiteSpace(name)) return;

        var category = (editing ?? new CategoryModel()) with
        {
            Name = name,
            Description = description,
            ParentCategoryId = parentId,
            IsActive = editing?.IsActive ?? true,
            SortOrder = editing?.SortOrder ?? 0
        };

        try
        {
            if (editing?.Id is not null)
            {
                await CategoryService.UpdateAsync(category, editing.Version, ct);
            }
            else
            {
                await CategoryService.CreateAsync(category, ct);
            }
        }
        catch (ProblemDetailsException ex) when (ex.StatusCode == 412)
        {
            // Notification already shown by ProblemDetailsDelegatingHandler; refresh the list
            // so the stale row the user was editing reflects what changed elsewhere.
        }

        await ResetEditor(ct);
        await CategoriesVersion.UpdateAsync(version => version + 1, ct);
    }

    /// <summary>Provides the start edit operation for category tree model.</summary>
    public async ValueTask StartEdit(CategoryModel category, CancellationToken ct)
    {
        _editingCategory = category;
        await NewCategoryName.UpdateAsync(_ => category.Name, ct);
        await NewCategoryDescription.UpdateAsync(_ => category.Description ?? string.Empty, ct);
        await SelectedParentId.UpdateAsync(_ => category.ParentCategoryId, ct);
        await IsEditing.UpdateAsync(_ => true, ct);
        await IsCreating.UpdateAsync(_ => false, ct);
    }

    /// <summary>Provides the cancel edit operation for category tree model.</summary>
    public async ValueTask CancelEdit(CancellationToken ct) => await ResetEditor(ct);

    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    public async ValueTask DeleteCategory(CategoryModel category, CancellationToken ct)
    {
        if (category.Id is null) return;
        try
        {
            await CategoryService.DeleteAsync(category.Id.Value, category.Version, ct);
        }
        catch (ProblemDetailsException ex) when (ex.StatusCode == 412)
        {
            // Notification already shown by ProblemDetailsDelegatingHandler; refresh either way.
        }
        await CategoriesVersion.UpdateAsync(version => version + 1, ct);
    }

    /// <summary>Provides the reset editor operation for category tree model.</summary>
    private async ValueTask ResetEditor(CancellationToken ct)
    {
        _editingCategory = null;
        await NewCategoryName.UpdateAsync(_ => string.Empty, ct);
        await NewCategoryDescription.UpdateAsync(_ => string.Empty, ct);
        await SelectedParentId.UpdateAsync(_ => null, ct);
        await IsEditing.UpdateAsync(_ => false, ct);
        await IsCreating.UpdateAsync(_ => true, ct);
    }
}
