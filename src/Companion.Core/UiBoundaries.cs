namespace Bap.Companion.Core;

// UI is intentionally outside the protocol core. A future WinUI/Tray process can implement these contracts.
public interface ITrayController
{
    Task SetStatusAsync(string status, CancellationToken cancellationToken = default);
    Task ShowNotificationAsync(string title, string message, CancellationToken cancellationToken = default);
}

public interface ICompanionGui
{
    Task RunAsync(CancellationToken cancellationToken = default);
}
