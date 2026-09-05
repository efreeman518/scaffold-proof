using CommunityToolkit.Mvvm.Messaging;
using TaskFlow.Uno.Core.Business.Models;
using TaskFlow.Uno.Core.Business.Notifications;
using TaskFlow.Uno.Core.Business.Services;

namespace TaskFlow.Uno.Presentation.Presentation;

/// <summary>Drives tag management state, navigation, and commands for the Uno presentation layer.</summary>
public partial record TagManagementModel(
    INavigator Navigator,
    ITagApiService TagService,
    IMessenger Messenger)
{
    private TagModel? _editingTag;

    public IState<int> TagsVersion => State<int>.Value(this, () => 0);

    public IListFeed<TagModel> Tags => ListFeed.Async(async ct =>
    {
        _ = await TagsVersion;
        return (IImmutableList<TagModel>)(await TagService.SearchAsync(ct: ct)).ToImmutableList();
    });

    public IState<string> NewTagName => State<string>.Value(this, () => string.Empty);
    public IState<string> NewTagColor => State<string>.Value(this, () => "#3B82F6");

    /// <summary>Creates requested data after validation and maps the result to the caller contract.</summary>
    public async ValueTask CreateTag(CancellationToken ct)
    {
        var name = await NewTagName;
        var color = await NewTagColor;
        if (string.IsNullOrWhiteSpace(name)) return;

        await TagService.CreateAsync(new TagModel { Name = name, Color = color }, ct);
        await NewTagName.UpdateAsync(_ => string.Empty, ct);
        await TagsVersion.UpdateAsync(version => version + 1, ct);
    }

    /// <summary>Updates existing data after validation and preserves domain invariants.</summary>
    public async ValueTask UpdateTag(CancellationToken ct)
    {
        var editing = _editingTag;
        if (editing?.Id is null) return;

        try
        {
            await TagService.UpdateAsync(editing, editing.Version, ct);
        }
        catch (ProblemDetailsException ex) when (ex.StatusCode == 412)
        {
            // Notification already shown by ProblemDetailsDelegatingHandler; refresh either way.
        }
        _editingTag = null;
        await TagsVersion.UpdateAsync(version => version + 1, ct);
    }

    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    public async ValueTask DeleteTag(TagModel tag, CancellationToken ct)
    {
        if (tag.Id is null) return;
        try
        {
            await TagService.DeleteAsync(tag.Id.Value, tag.Version, ct);
        }
        catch (ProblemDetailsException ex) when (ex.StatusCode == 412)
        {
            // Notification already shown by ProblemDetailsDelegatingHandler; refresh either way.
        }
        await TagsVersion.UpdateAsync(version => version + 1, ct);
    }

    /// <summary>Provides the start edit operation for tag management model.</summary>
    public ValueTask StartEdit(TagModel tag, CancellationToken ct)
    {
        _editingTag = tag;
        return ValueTask.CompletedTask;
    }

    /// <summary>Provides the cancel edit operation for tag management model.</summary>
    public ValueTask CancelEdit(CancellationToken ct)
    {
        _editingTag = null;
        return ValueTask.CompletedTask;
    }
}
