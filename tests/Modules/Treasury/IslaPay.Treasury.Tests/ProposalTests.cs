using System.Security.Claims;
using IslaPay.Platform;
using IslaPay.TestSupport;
using IslaPay.Treasury.Contracts;
using Microsoft.AspNetCore.Http;

namespace IslaPay.Treasury.Tests;

/// <summary>
/// The four-eyes rule, below the roles.
/// </summary>
/// <remarks>
/// The roles already keep a proposer from approving: the two permissions live
/// in roles that cancel each other. These prove the service refuses on its
/// own, so a mistake in the role table does not become a way to approve one's
/// own money.
/// </remarks>
[Collection(TreasuryDefinition.Name)]
[Trait("Category", "Integration")]
public class ProposalTests
{
    private readonly TreasuryFixture _postgres;

    public ProposalTests(TreasuryFixture postgres) => _postgres = postgres;

    private TreasuryProposals Proposals(TimeProvider? clock = null)
    {
        Skip.IfNot(_postgres.Available, "No Postgres reachable.");
        return _postgres.Proposals(clock);
    }

    private static CreditRequest Credit(string amount = "10.00") =>
        new("float", Money.Parse(amount, TestCurrencies.EIsla), "capital", "Fondeo de prueba");

    [SkippableFact]
    public async Task Nobody_approves_their_own_proposal_whatever_they_hold()
    {
        var proposals = Proposals();
        var ana = Person("ana");

        var proposal = await proposals.ProposeCreditAsync(ana, Credit(), Guid.NewGuid().ToString("N"));

        var refusal = await Assert.ThrowsAsync<TreasuryException>(
            () => proposals.ApproveAsync(ana, proposal.Id, null));
        Assert.Equal(TreasuryErrors.OwnProposal, refusal.Code);

        var rejection = await Assert.ThrowsAsync<TreasuryException>(
            () => proposals.RejectAsync(ana, proposal.Id, "no me gusta"));
        Assert.Equal(TreasuryErrors.OwnProposal, rejection.Code);

        Assert.Equal(TreasuryProposalStatuses.Pending, (await proposals.GetAsync(proposal.Id)).Status);
    }

    [SkippableFact]
    public async Task Only_the_proposer_withdraws()
    {
        var proposals = Proposals();
        var proposal = await proposals.ProposeCreditAsync(
            Person("ana"), Credit(), Guid.NewGuid().ToString("N"));

        var refusal = await Assert.ThrowsAsync<TreasuryException>(
            () => proposals.WithdrawAsync(Person("beto"), proposal.Id));
        Assert.Equal(TreasuryErrors.NotYourProposal, refusal.Code);

        var withdrawn = await proposals.WithdrawAsync(Person("ana"), proposal.Id);
        Assert.Equal(TreasuryProposalStatuses.Withdrawn, withdrawn.Status);

        var late = await Assert.ThrowsAsync<TreasuryException>(
            () => proposals.ApproveAsync(Person("beto"), proposal.Id, null));
        Assert.Equal(TreasuryErrors.ProposalNotPending, late.Code);
    }

    [SkippableFact]
    public async Task A_proposal_left_for_a_day_expires_and_cannot_be_approved()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var proposals = Proposals(clock);
        var proposal = await proposals.ProposeCreditAsync(
            Person("ana"), Credit(), Guid.NewGuid().ToString("N"));

        clock.Advance(TreasuryProposals.Lifetime + TimeSpan.FromMinutes(1));

        var refusal = await Assert.ThrowsAsync<TreasuryException>(
            () => proposals.ApproveAsync(Person("beto"), proposal.Id, null));
        Assert.Equal(TreasuryErrors.ProposalNotPending, refusal.Code);
        Assert.Equal(TreasuryProposalStatuses.Expired, (await proposals.GetAsync(proposal.Id)).Status);
    }

    [SkippableFact]
    public async Task A_proposal_is_checked_when_it_is_made_not_only_when_it_is_approved()
    {
        var proposals = Proposals();

        var refusal = await Assert.ThrowsAsync<TreasuryException>(() => proposals.ProposeCreditAsync(
            Person("ana"),
            new CreditRequest("fees", Money.Parse("10.00", TestCurrencies.EIsla), "capital", "Mal destino"),
            Guid.NewGuid().ToString("N")));
        Assert.Equal(TreasuryErrors.UnknownDestination, refusal.Code);
    }

    private static DefaultHttpContext Person(string subject) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", subject), new Claim("preferred_username", $"{subject}@islapay.cu")], "test")),
    };

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
