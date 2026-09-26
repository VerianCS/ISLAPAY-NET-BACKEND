using IslaPay.Platform.Api;

namespace IslaPay.Treasury.Contracts;

/// <summary>What the treasury refuses, and why.</summary>
/// <remarks>
/// Every one of these is seen only by an operator, never by a customer. That
/// changes what a good message is: "there is no such account" is exactly the
/// information wanted by somebody whose job is to name accounts, and hiding it
/// — as the customer-facing modules rightly do — would only make the console
/// harder to use without protecting anything.
/// </remarks>
public static class TreasuryErrors
{
    /// <summary>The credit named somewhere money cannot be put.</summary>
    /// <remarks>
    /// Only the float and the settlement fund can be credited from outside.
    /// Not escrow — that is money held for a named order and creating some
    /// without an order is how a reconciliation stops meaning anything — and
    /// not fees, which are earned rather than deposited.
    /// </remarks>
    public const string UnknownDestination = "unknown_destination";

    /// <summary>The credit is zero, negative, or missing its reason or source.</summary>
    public const string InvalidCredit = "invalid_credit";

    /// <summary>The account named in a path is not one the chart has.</summary>
    public const string UnknownAccount = "unknown_account";

    /// <summary>No proposal has that id.</summary>
    public const string ProposalNotFound = "proposal_not_found";

    /// <summary>
    /// The proposal was already approved, rejected, withdrawn or has expired.
    /// <c>meta.status</c> says which.
    /// </summary>
    public const string ProposalNotPending = "proposal_not_pending";

    /// <summary>
    /// The caller proposed this and may not also approve or reject it.
    /// </summary>
    /// <remarks>
    /// The whole point of a second person. Refused whatever roles the caller
    /// holds, so a mis-granted pair of roles still cannot approve its own work.
    /// </remarks>
    public const string OwnProposal = "own_proposal";

    /// <summary>Only whoever proposed something may withdraw it.</summary>
    public const string NotYourProposal = "not_your_proposal";
}

/// <summary>A refusal the platform can translate without knowing this module.</summary>
public sealed class TreasuryException : Exception, IApiFailure
{
    public TreasuryException(
        string code, int status, string message, IReadOnlyDictionary<string, object>? meta = null)
        : base(message)
    {
        Code = code;
        Status = status;
        Meta = meta;
    }

    public string Code { get; }

    public int Status { get; }

    public IReadOnlyDictionary<string, object>? Meta { get; }
}
