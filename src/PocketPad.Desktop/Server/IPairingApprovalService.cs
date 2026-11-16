namespace PocketPad.Desktop.Server;

public sealed record PairingApprovalRequest(string DeviceName, string Platform, string RemoteAddress);

/// <summary>Asks the Windows user whether a phone may pair. Implemented by the UI.</summary>
public interface IPairingApprovalService
{
    /// <summary>True only if the user explicitly approved. Cancellation (timeout, phone gone) must close the prompt.</summary>
    Task<bool> RequestApprovalAsync(PairingApprovalRequest request, CancellationToken cancellationToken);
}
