using System.ComponentModel;

namespace QwenTray.Services;

/// <summary>
/// Whether something is working the distro as a whole. While it is held, nothing else may run inside it,
/// because a hold is how the distro gets stopped, unregistered, or written over from outside.
/// </summary>
public interface IDistroHold : INotifyPropertyChanged
{
    bool IsHeld { get; set; }
}

/// <summary>
/// Shared state, so the page that takes a hold and the pages that wait for one need no link to each
/// other. Any command that stops the distro, unregisters it, or writes into it wholesale takes this.
/// </summary>
public sealed partial class DistroHold : ObservableObject, IDistroHold
{
    [ObservableProperty]
    private bool _isHeld;
}
