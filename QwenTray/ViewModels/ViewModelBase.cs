using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;

namespace QwenTray.ViewModels;

public interface IViewModelBase
{
    // Annotated so the view locator can activate the view through the container under trimming.
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    Type ViewType { get; }

    Task InitializeAsync();
}

public abstract class ViewModelBase<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TView> : ObservableObject, IViewModelBase
    where TView : Control
{
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type ViewType => typeof(TView);

    public Task InitializeAsync()
    {
        return OnInitializeAsync();
    }

    protected virtual Task OnInitializeAsync()
    {
        return Task.CompletedTask;
    }
}