namespace TaskFlow.Application.Contracts.Messaging;

/// <summary>Outcome of <see cref="IInboxStore.TryClaimAsync"/>.</summary>
/// <param name="Status">What the claim attempt found.</param>
/// <param name="ClaimToken">Token guarding complete and release; <see cref="Guid.Empty"/> unless <see cref="InboxClaimStatus.Acquired"/>.</param>
public readonly record struct InboxClaim(InboxClaimStatus Status, Guid ClaimToken);
