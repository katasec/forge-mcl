namespace ForgeMission.Core.Tools;

// A surface that can ask the user implements this; without one, a request that needs
// confirmation is treated as declined. The dispatcher only needs a yes/no
// answer for one already-policy-evaluated request; it never grants implicit session-wide consent.
public interface ICapabilityConfirmationHandler
{
    Task<bool> ConfirmAsync(CapabilityConfirmationRequest request, CancellationToken ct);
}

public sealed record CapabilityConfirmationRequest(string CapabilityName, string RequestSummary);
