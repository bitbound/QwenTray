using System.ComponentModel;

namespace QwenTray.Services;

/// <summary>
/// Whether a backup currently holds the distro from outside it. While it is held, nothing that writes
/// into the distro may run, so every page that reaches inside gates on this.
/// </summary>
public interface IBackupHold : INotifyPropertyChanged
{
    bool IsHeld { get; set; }
}

/// <summary>
/// Shared state, so the page that raises a hold and the pages that wait for one need no link to each
/// other.
/// </summary>
public sealed partial class BackupHold : ObservableObject, IBackupHold
{
    [ObservableProperty]
    private bool _isHeld;
}
