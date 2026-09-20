# State of the system

What actually works, module by module, as of the `islapay-contracts` branch.

`ARCHITECTURE.md` describes the shape the system is meant to have. This
describes the one it has. Where they disagree, this file is right.

The distinction that matters throughout: a module can exist as a directory, as
a set of contracts, as an implementation, and as something a phone can reach.
Those are four different things and several modules are only the first two.

---

## Summary

| Module | Routes | Tables | Tests | State |
|---|---|---|---|---|
| Identity | 7 | — (Keycloak owns them) | 38 | **Works** |
| Ledger | 0 | 4 | 25 + 15 domain | **Works**, no HTTP surface by design |
| Wallet | 3 | — (reads the ledger) | 8 | **Works** |
| Marketplace | 10 | 2 | 23 | **Works** |
| Exchange | 0 | 0 | 4 | Contracts only |
| P2P | 0 | 0 | 3 | Contracts only |

Platform: Api (4 tests), Data, Messaging (8), Money (19), AspNet, Serialization.
End to end: 36. Architecture: 8. **212 in total**, against real Postgres,
RabbitMQ and Keycloak.

What the whole thing cannot do: **take money in or pay money out.** Every
balance in every test is created by a test posting a settlement against the
platform's float. There is no recharge endpoint, no deposit address, no payout.
That is the gap that decides whether any of the rest is a product.

---

## Identity — works

Register, sign in, refresh, sign out, prove a phone by OTP, reset a password.
Backed by a real Keycloak; these are Keycloak's own tokens, passed through
unchanged rather than re-signed.

**Owns no tables.** The account lives in Keycloak, which is the point: there is
one place that stores a password hash and it is not ours.

**Integrates** two ways. It publishes `user.registered.v1` through the outbox,
and it exposes `IUserDirectory` — read-only, so nothing outside Identity can
create, disable or edit an account. Wallet and Marketplace both use the
directory to turn an id into a name and to read `phoneVerified`.

**Not real yet:** OTP codes are delivered by an `IOtpSender` that has no
production implementation, and the host refuses to start without one outside
Development. In Development it logs the code. The OTP challenges themselves
live in **process memory**, which is correct for one instance and silently
wrong for two — this is the single thing that pins the backend to a single
replica.

---

## Ledger — works

Double-entry in Postgres. Four tables: `accounts`, `postings`, `entries`,
`balances`. Entries and postings are append-only, enforced by triggers rather
than by convention. The per-account sequence is gapless, so a removed row is
detectable even if it was removed outside the application. Balances are
materialised and locked with `FOR UPDATE` in a fixed order before anything is
read, which is what stops two concurrent postings both deciding there is enough.

**No routes, deliberately.** A ledger is not something a client talks to. It
has no view a user would recognise; Wallet is what turns entries into a screen.

**Integrates** through `ILedger`: `EnsureUserAccountsAsync`, `BalancesAsync`,
`EntriesAsync`, `PostAsync`, `FindPostingAsync`. A posting may carry
`PendingEvent`s that are written to the outbox inside the posting's own
transaction, so an announced movement cannot be one that did not happen.

The chart of accounts already contains more than is used: `SettlementFund`,
`CashFloat` and `External` exist and are reached only by tests. They are the
accounts a recharge and a payout will post against.

**Not real yet:** nothing. This module does what it says. What is missing is
around it, not in it — there is no notion of a *pending* posting, which a real
payment processor needs (they settle at T+1 and can reverse).

---

## Wallet — works

`GET /v1/me/wallet`, `GET /v1/me/transactions`, `POST /v1/transfers`.

**Owns no tables.** Its state is the ledger's. Giving it a second copy would
create two answers to "what is my balance", and that is the kind of thing a
customer discovers.

**Integrates** by consuming `user.registered.v1` to open a new user's accounts,
and by opening them again on the first wallet read — the event is the fast
path, the read is the guarantee, because Keycloak owns the account and this
database cannot join the transaction that created it. Both are tested, the
second with the broker deliberately unreachable.

**Not real yet:** the exchange rates in the wallet response are hard-coded
parity for three dollar-denominated instruments. It says so in the code. They
become a call to Exchange the day Exchange exists.

It publishes `transfer.completed.v1`. **Nothing consumes it** — see Messaging
below.

---

## Marketplace — works

Ten routes: publish, browse, read, withdraw, my listings; place an order,
redeem a code, cancel, read an order, my orders.

A seller lists an item. A buyer locks the price — the money genuinely moves
into escrow, it is not a flag on a row — and gets a single-use code. The seller
scans it and escrow pays out, less 1%. Nobody scans it in 72 hours and a
sweeper returns it.

**Owns two tables**, `listings` and `orders`, and is the first module to use EF
Core — as a mapper only. The schema is still the `.sql` under `Migrations/`,
and the statements where a row lock is the point are raw SQL through `FromSql`.

**Integrates** with the ledger for every movement and with Identity for names
and the phone gate. It is the module that had to face the seam: the ledger owns
its transaction and will not hand it out, so an order's status and the posting
that moved its money are two commits. Hence the in-flight statuses, the sweeper
that settles the question with `FindPostingAsync`, and the single idempotency
key shared by the two ways an order can end.

**Not real yet:** photos are URLs, and there is nowhere to upload one, so the
field is a shape rather than a feature. Categories and conditions are free
strings — there is no agreed server-side taxonomy and inventing one here would
be a second source of truth for a list the app already owns.

It publishes `order.held.v1`, `order.released.v1` and `order.refunded.v1`.
**Nothing consumes them.**

**And it has no client at all.** This is the largest single gap in the product:
the whole flow exists server-side and tested, and the phone has no screen for
it and no package that can read a QR code.

---

## Exchange — contracts only

85 lines of DTOs, 4 tests over them, no implementation, no routes, no tables.
`API_CONTRACT.md` agrees the shapes of `/v1/exchange/quotes` and
`/v1/exchange/conversions`; nothing serves them.

The posting structure is written and tested — `Conversions.BuildConversion` in
the ledger's domain, with the fee in basis points and a fee leg that is omitted
rather than posted as zero when it rounds away. So the money part exists; the
quote, the rate source and the endpoint do not.

---

## P2P — contracts only

52 lines of DTOs, 3 tests, nothing else. `BuildP2PSale` exists in the ledger's
domain in the same way.

Worth being clear about what this is, because the name misleads: it is instant
exchange **against IslaPay** at a published rate, not a user-to-user market.
There is no counterparty, no escrow and no dispute. It is also the intended
cash-out rail, which makes it the most important unimplemented module in the
repository.

---

## Platform

**Data.** Npgsql, explicit transactions, and a migrator that applies embedded
`.sql` in name order under an advisory lock, recording a checksum per script.
Editing an applied script is refused. Works.

**Messaging.** Transactional outbox plus RabbitMQ: quorum queues, dead-letter
queues, publisher confirms, and a topology declared before anything publishes.
Works — and there is now a test that drains the outbox for real and asserts
nothing is left unpublished, because every other test runs with background
publishing off and would not have noticed.

The honest part: **exactly one subscription exists.** Wallet consumes
`identity.user.*.v1`. Queues are created only for declared subscriptions, so
`x.wallet` and `x.marketplace` are declared, receive their messages, and drop
them for want of a binding. Four event types are published into nothing:
`transfer.completed.v1`, `order.held.v1`, `order.released.v1`,
`order.refunded.v1`.

That is not a bug. It is a notifications module that does not exist yet, and
the events are correct and durable in the meantime. But nobody should read
"publishes an event" as "something happens".

**Api.** `ApiProblem`, `CursorPage`, platform error codes, `IApiFailure`.
References nothing at all, by rule.

**AspNet.** The module contract, the shared pipeline, health endpoints, and
HTTP idempotency persisted in `platform.idempotency` and scoped per caller. A
4xx is recorded and replayed; a 5xx releases the key.

**Money.** Integer minor units, basis-point fees, explicit rounding. 19 tests
including property tests.

---

## Compliance, honestly

There is none. No KYC, no sanctions screening, no transaction limits, no
monitoring, no suspicious-activity workflow, and no originator or beneficiary
data on a transfer (FATF R.16). `TransferRequest` carries an amount, a
destination and a note.

The one thing a supervisor would like is the ledger: append-only, gapless,
fully audited.

This is not on the roadmap below because it is not a coding decision first —
see the sanctions discussion; the company's structure decides the architecture,
not the other way round.

---

## Operations

No Dockerfile for the application. The `docker-compose.yml` brings up
dependencies for development, not the system. No tracing, no metrics, no
backups, no runbook, no HTTP rate limiting, no CORS policy. CI builds and tests
against real Postgres, RabbitMQ and Keycloak on every push, and that is the
whole of the automation.

---

## The client, in one paragraph

Flutter, 50 live screens, and `USE_MOCKS` defaults to **true**. Authentication
is wired to this backend for real — token pair, keystore, pre-emptive refresh,
single-flight, one replay on a 401 — and nothing else is. Every other
repository answers from an in-memory mock, and `ApiService`'s remaining paths
point at endpoints this backend does not serve. 52 analyzer exclusions cover
111 files that no longer compile since the rework dropped `flutter_bloc` and
`get_it`; they should be deleted rather than excluded.

---

## What makes this functional, in order

1. **A way in.** An admin-issued credit endpoint — not a payment rail, but
   what a pilot actually uses. `BuildDeposit` already exists and nothing calls it.
2. **Wallet on the phone.** Fix the client's paths, map the DTOs, send the
   idempotency key.
3. **Marketplace on the phone.** A QR scanner, and the five screens the flow
   needs.
4. **Somewhere to run.** Delete the dead Dart, add a Dockerfile, bring the
   whole thing up with one command.

After that, and only after that, the question is which of Exchange, P2P,
recharge and payout comes first — and that answer depends on the company's
legal structure rather than on this repository.
