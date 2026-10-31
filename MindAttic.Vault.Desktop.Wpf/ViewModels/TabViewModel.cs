using MindAttic.Components.Wpf.Controls;
using MindAttic.Components.Wpf.Mvvm;
using MindAttic.Vault.Desktop.Profiles;

namespace MindAttic.Vault.Desktop.ViewModels;

/// <summary>One tab in the window. Every tab reloads from the selected Profile.</summary>
public abstract class TabViewModel : ObservableObject
{
    private string statusText = "";
    private string? notice;

    /// <summary>Stable id: <c>getstarted</c>, <c>keys</c>, or <c>app:{AppId}</c>. Remembered as the last tab.</summary>
    public abstract string Id { get; }

    public abstract string Title { get; }

    /// <summary>Short text after the title (e.g. "unsaved").</summary>
    public virtual string? Trailing => null;

    /// <summary>A status light on the tab, if any.</summary>
    public virtual MaStatus? Status => null;

    /// <summary>What the last action did, shown under the tab's header.</summary>
    public string StatusText
    {
        get => statusText;
        set => Set(ref statusText, value);
    }

    /// <summary>Why the tab can't show values right now (Profile not ready, access denied), or null.</summary>
    public string? Notice
    {
        get => notice;
        protected set
        {
            if (Set(ref notice, value)) Raise(nameof(HasNotice));
        }
    }

    public bool HasNotice => !string.IsNullOrEmpty(Notice);

    /// <summary>Loads everything from <paramref name="profile"/>; <paramref name="readiness"/> says whether it can.</summary>
    public abstract Task LoadAsync(IProfile profile, ProfileReadiness readiness);

    protected void RaiseTabState()
    {
        Raise(nameof(Trailing));
        Raise(nameof(Status));
    }
}
