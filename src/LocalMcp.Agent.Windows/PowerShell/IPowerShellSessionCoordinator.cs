namespace LocalMcp.Agent.Windows.PowerShell;

/// <summary>
/// Public coordination interface so that <see cref="Worker"/> can trigger
/// session cancellation on shutdown without directly referencing the internal
/// <see cref="PowerShellSessionRegistry"/> concrete type.
/// </summary>
public interface IPowerShellSessionCoordinator
{
    void CancelAll();
}
