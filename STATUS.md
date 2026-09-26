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
| Catalog | 5 | 3 | 14 | **Works** |
| Identity | 7 | — (Keycloak owns them) | 38 | **Works** |
| Ledger | 0 | 4 | 16 + 15 domain | **Works**, no HTTP surface by design |
| Wallet | 3 | — (reads the ledger) | 8 | **Works** |
| Marketplace | 10 | 2 | 23 | **Works** |
| P2P | 17 | 3 | 41 | **Works** — needs an operator |
| Custody | 3 | 2 | 20 | **Works** — needs a custodian |
| Treasury | 4 | 0 (reads the ledger) | 16 | **Works** — needs a console |
| Exchange | 0 | 0 | 4 | Contracts only |

Platform: Api (4 tests), Data, Messaging (8), Money (65), AspNet, Serialization.
End to end: 65. Architecture: 8. **337 in total**, against real Postgres,
RabbitMQ and Keycloak, plus three capture tools that only run when asked.

One of the end-to-end tests is the whole journey rather than a slice:
`PurchaseJourneyTests` registers two strangers, proves both phones, signs in
with a password, publishes an item, browses to it, pays for it and collects
it — and narrates each step with the real figures. A system can pass every
slice and still have no journey a person can complete; that test is the
difference. Run it with `-l "console;verbosity=detailed"` to read the
transcript.

What the whole thing can now do that it could not: **pay money out and take it
in**, through P2P, provided a person settles the local leg, and **say where
its own money is and put more in** — the settlement fund and the float are
topped up through `POST /v1/admin/treasury/credits` rather than by posting to
the ledger by hand, and escrow is reconciled against what each module says it
is holding. What it still cannot do is settle that local leg itself: there is
no Transfermóvil integration and no console for the operator to work the queue
in. The rail exists; the hands on it do not.

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

## Catalog — currencies and chains, as tables

The change everything multi-currency was waiting on. `Currency` was an `enum`
with four members, which meant the set of currencies was decided at compile
time: adding the Mexican peso was a deploy, adding forty of them was a deploy
and a migration, and switching one off for a jurisdiction was not expressible
at all.

It is now a `readonly record struct` carrying a code and a scale, and the set
lives in three tables — `catalog.currencies`, `catalog.networks` and
`catalog.currency_networks`. The seed lists **4 enabled currencies** (E-ISLA,
USDT, USDC, CUP) and **35 more switched off**: 23 from the Americas, 11 from
Europe, and the yuan. Six chains, of which TRON is watched, and eight
asset-on-chain pairs with real contract addresses, of which USDT-on-TRON is
switched on.

Three decisions carry the rest:

**A currency and a chain are different things, and so is their pair.** USDT is
not a TRON token — it is both a TRON token and an Ethereum one, and they are
different assets that share a name and a price. Sending Ethereum USDT to a TRON
address loses the money. `catalog.currency_networks` is what makes "USDT on
TRON" a thing rather than a phrase, and it is why a deposit address is now
asked for as `/v1/me/custody/addresses/{currency}/{network}` rather than by
chain alone.

**A stored amount says what a unit is.** Minor units plus a code do not say
whether `1500000` is one and a half USDT or a million and a half; the enum used
to answer that. Every table holding an amount now carries the scale beside the
code, so a row read years from now means what it meant when it was written.

**A scale can never change.** A trigger on `catalog.currencies` refuses any
update that would alter one. Changing USDT from six places to two would not
reprice anything — it would silently restate every stored balance by a factor
of ten thousand, and no migration, script or console session can do it by
accident.

**Enforced at the ledger, not at each caller.** `PostgresLedger` takes the
catalogue and refuses a posting — or an account — in a currency that is not
listed or is switched off, and refuses a leg whose scale disagrees with the
table even though it balances perfectly. Six modules each checking would be six
checks, and the forgotten one would be the one that mattered. Reading is
different: `Describe` answers for a disabled currency, because a statement
containing an old entry has to keep working after a currency is withdrawn.

**Switching one on takes no deploy**, which is the whole point, and there is an
end-to-end test that proves it: a `catalog-admin` flips MXN on through
`/v1/admin/catalog/currencies/MXN/enabled`, and the same running process, the
same ledger objects, start accepting it.

**Not real yet:** the Flutter client still has its own `Currency` enum and
still hard-codes three currencies. It should read `/v1/catalog/currencies` —
that is the next piece of this work, and until it lands, switching a currency
on server-side does not make it appear in the app.

---

## E-ISLA — the internal unit, renamed

`Currency.Usd` is now `Currency.EIsla`, code `EISLA`, two decimals. It is the
same unit it always was: an internal liability of IslaPay, on no chain,
redeemable one for one against a stablecoin subject to the fund having it. What
changed is that it no longer claims to be a US dollar on every screen. The
client's mock backend had said as much for months — *"the wallet settles it on
the IslaPay (ISLA, 1:1 USD) balance"* — so this makes the server agree with
what the app was already describing.

Two migrations do the data, `ledger/002_eisla.sql` and `p2p/002_eisla.sql`.
The first is the interesting one: `ledger.entries` is append-only by trigger,
and a rename cannot be done by posting a reverse. The script drops the trigger,
rewrites the label, puts the trigger back, and then refuses to finish if either
a `USD` row survived or the trigger did not come back. Four tests in
`EIslaRenameTests` run the shipped script over rows that really are in USD —
every other test here runs against a database created a moment earlier, where
it would sail over empty tables and prove only that it parses.

The old code is **rejected**, not aliased, and where it is rejected moved when
currencies became rows. It used to fail while reading the body, because no
`USD` existed to read it as. Now the dollar is a listed currency — switched
off, because IslaPay does not issue US dollars — so the body parses and the
ledger declines the posting: `422 currency_unavailable`, with the code in
`meta`. The refusal is the same and its reason is better. An end-to-end test
holds it open.

Fixing that test found something else, which was not this change's fault but
was in its way: a malformed body did not produce an `ApiProblem` at all. The
platform caught `JsonException`, but minimal-API parameter binding wraps the
converter's exception in `BadHttpRequestException` before it gets there, so the
response was `text/plain` with a .NET stack trace in it — unparseable by the
client, and a description of the server's internals to anybody who sent a bad
body. Both are caught now.

**Issued, since this change, from one place.** The ledger has an issuer
account, `platform:issuer:EISLA`; its balance negated is all the E-ISLA in
existence. `POST /v1/admin/treasury/mints` and `/burns` create proposals that a
second person approves, like a credit; a mint that would leave E-ISLA
outstanding beyond the USDT and USDC IslaPay holds in its own name (float,
settlement fund, fees — not escrow) is refused at approval
(`reserve_insufficient`, `meta.headroom`), one issuance at a time. E-ISLA can no
longer be credited from a mirror (`not_creditable`). `GET
/v1/admin/treasury/issuance` reports what is outstanding, the reserves, the
headroom and any account holding E-ISLA it did not get from the issuer. At
start-up, the E-ISLA that existed before the issuer (the negatives of the float
and the mirrors) is moved onto it in one posting, once.

What this does not do: stop a module or a test from posting E-ISLA out of the
float directly — the ledger allows platform accounts to go negative — so the
report lists those as strays rather than the ledger refusing them.

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
parity for the three currencies a customer can hold. It says so in the code.
They become a call to Exchange the day Exchange exists.

They are now built from `OpenedOnRegistration` rather than typed out, which is
worth a sentence because typing them out had gone wrong twice at once. The
keys still read `USD_USDC` long after USD became E-ISLA, and only three of the
six ordered pairs were listed. Neither failed anything: the client falls back
to a rate of one for a key it cannot find, and a fallback of one is
indistinguishable from parity right up until the day parity ends. An
end-to-end test now asserts the exact six keys.

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

## Exchange — works

`POST /v1/exchange/quotes` and `POST /v1/exchange/conversions` (⚿), between
whatever a customer holds and is switched on: E-ISLA, USDT, USDC.

- **Price.** Rate 1 (all three are worth a dollar); fee 1 % in the currency
  given, to `fees`; what arrives is rounded **down** to the target's decimals,
  and the fraction below stays in the fund. The settlement fund takes what is
  given and pays what arrives: converting USDT into E-ISLA puts the USDT in the
  reserve the issuer counts.
- **Quotes are not stored.** A quote id is the quote, signed with the host's
  data-protection keys: the person, the pair, the amount and a 30-second
  expiry. A conversion carries only the id, so nobody executes at figures
  they were not shown, and abandoned quotes need no sweeping. Several
  instances must share the key ring.
- **Once per quote.** The posting is keyed by the quote, so the same quote sent
  again with a new `Idempotency-Key` answers `applied: false` and moves nothing.
- **Not executable** is a normal answer with a reason: `insufficient_funds`,
  `fund_unavailable` (the fund cannot pay; E-ISLA is minted to stock it),
  `phone_not_verified`, `account_frozen`. Converting is not capped by the
  verification level: nothing leaves the person's hands.
- The wallet's `rates` now come from the exchange (`IExchangeRates`).

---

## P2P — works, and waits for a person

Seventeen routes: the rails and their rates, a quote, open a trade, read it,
cancel it, list your own — and eleven for whoever settles them.

Worth being clear about what this is, because the name misleads: it is instant
exchange **against IslaPay** at a published rate, not a user-to-user market.
There is no counterparty and no dispute.

"Instant" describes the price, not the settlement, and that is the whole
design. A **sell** debits the wallet immediately and moves the counter-value
out of the settlement fund into escrow, where it is explicitly the customer's
until an operator confirms the Transfermóvil transfer. A **buy** posts nothing
at all until an operator confirms the local money arrived — money is never
credited before it exists.

Every trade posts across two currencies in one posting, and each currency
balances on its own. That is what `Currency.Cup` was added for: the obligation
to send somebody pesos is a liability, and a liability belongs in the ledger
rather than in a column somewhere. No customer holds a CUP balance and none
ever will — it is absent from `WalletService.OpenedOnRegistration` and
`IsCustomerHoldable` says so. CUP is also confined to this module: the exchange
and custody, when they arrive, hold escrow in E-ISLA, USDC and USDT only.

**Owns three tables.** `methods` (rails), `rates` (appended, never updated, so
a trade settled last Tuesday is still explicable at the price it was given)
and `trades`.

**A rail is a currency, not a channel.** There is one `cup` rail, whichever
app the pesos move through — where to pay is what its instructions say. The
wallet side is whatever the catalogue marks customer-holdable (today E-ISLA,
USDT and USDC), each with its own sell and buy price on the same rail; a side
left unpublished is not offered, and publishing an empty rate withdraws one.
Limits are in the local currency (500 to 60,000 CUP), because the peso leg is
what the desk actually moves. Migration 005 turned `cup_transfermovil` into
`cup` and pointed its rates and trades at it; trades keep the name they were
placed under.

**The desk edits rails without SQL:** `GET`/`POST /v1/admin/p2p/methods`
lists and opens rails (one per fiat currency the catalogue has switched on,
created off and unpriced), `PATCH /v1/admin/p2p/methods/{id}` renames one or
moves its limits. **And finds what the queue does not show:**
`GET /v1/admin/p2p/trades?reference=&status=` — a buy that expired and was
paid late, which the receipt route still accepts, is found by the reference
the bank printed (with or without the dash, any case) or by `status=expired`.

Two things it does that are not obvious and are deliberate:

- **A committed sell never expires.** It has taken somebody's money, and the
  only honest endings are paying them or giving it back — neither of which a
  timer is entitled to decide. It waits for a person however long that takes,
  and the queue is where the wait is visible.
- **An expired buy can still be honoured.** Expiry means IslaPay has stopped
  expecting the money, not that it will refuse it. A buyer who transfers at the
  last minute and an operator who looks five minutes later would otherwise
  leave IslaPay holding pesos it has no way to account for.

**The solvency check is load-bearing.** The settlement fund is a platform
account and platform accounts may go negative, so nothing in the ledger stops
IslaPay promising a payout it cannot make. `ILedger.BalanceOfAsync` was added
for this and it is the only thing that refuses the trade.

**Where a seller is paid** travels with the sale: `payoutTo` on
`POST /v1/p2p/trades` is required for a sell (a card or phone, up to 64
characters), stored on the trade, and shown to the seller and in the
operator's queue. A sell used to record everything about the money except
where it was going, so the operator had a name and nothing to pay into.

**Where a buyer pays** is the rail's instructions, set with
`PUT /v1/admin/p2p/methods/{id}/instructions` and shown on every buy next to
its reference. The column existed from the start with nothing able to write
it, so until that route every buy told the buyer to pay and not where.

**Who may do what:** reading the queue and searching needs `p2p.read`;
paid, received and failed need `p2p.settle` (the `p2p-operator` role); prices,
methods, limits, instructions and on/off need `p2p.manage` (`p2p-manager`).
See *Staff roles and permissions* below. There is no Transfermóvil
integration — a person does it by hand and types the bank's reference back in.
The desk's CUP is funded through Treasury
(`POST /v1/admin/treasury/credits` into `settlement_fund`), like any other
currency.

**The app is wired to it** — methods, quotes, trades, cancel and history —
and its tests parse responses this module really produced:
`P2P_CAPTURE_PATH=<dir> dotnet test --filter
FullyQualifiedName~Capture_the_p2p_responses`.

It publishes `trade.committed.v1`, `trade.completed.v1` and
`trade.refunded.v1`. **Nothing consumes them** — the first is exactly what an
operator console should wake up on.

---

## Custody — works, and waits for a custodian

Three routes: the networks this build watches, the caller's deposit address on
one of them, and their deposits.

The first module whose main input is not a request. Everything else here moves
because somebody called an endpoint; a deposit moves because a stranger
broadcast a transaction, and the application finds out by reading a block. That
inverts the failure question from "did we do what was asked" to "have we
noticed what already happened, and exactly once".

**Owns two tables**, `addresses` and `deposits`, and no money. One address per
user, network and currency, issued once and kept — a fresh address per deposit
is better for privacy and worse for everything else, because people save an
address and send to it again.

Two rules run through all of it.

- **Nothing reaches the ledger before the chain is final.** Nineteen
  confirmations on TRON, which is where its own consensus stops being
  reversible. A transfer four blocks deep is one that can still be un-happened,
  and crediting it early means clawing a balance back from somebody who has
  already spent it. The rule is a check constraint, not just a branch: a row
  claiming to be credited below finality is refused by Postgres, so a future
  caller with a good reason has to argue with the database.
- **What does reach it lands exactly once.** The scanner re-reads blocks,
  restarts mid-range, and gets run twice by accident, so the chain's own
  identifier — `(network, tx hash, output)` — is unique, and seeing a transfer
  a hundred times is indistinguishable from seeing it once. There is a test
  that does exactly that.

The same interrupted-credit window as Marketplace and P2P, resolved the same
way: an in-flight status meaning "a posting for this may exist", one shared
idempotency key, and asking the ledger rather than guessing. One difference —
here a scanner pass resolves an in-flight deposit itself rather than waiting
for the sweeper, because a deposit sitting in `crediting` is somebody's money
not in their balance. And a stuck deposit is finished rather than merely
released: one already final and deep is one the scanner has no reason to report
again, so handing it back to `confirming` would leave it waiting forever.

A reorganisation before finality marks the deposit `orphaned`, and nothing is
reversed because nothing was posted. After crediting it is not undone at all:
past finality the loss, if there ever were one, is real and it is an operator's
problem, not a status change.

**Not real yet, and this is the whole of it: there is no custodian.** Addresses
come from `IDepositAddresses`, which has no production implementation — the
host refuses to start without one outside Development, where a deterministic
fake invents valid-looking addresses that no key exists for. That is a decision
rather than an omission. What sits behind that interface in production is
either a custodian or a key-management service, and the difference between them
is the difference between IslaPay holding customers' private keys and not.
Deriving keys in this process would make the application's memory, its crash
dumps, its logs and its backups all places a private key can leak from.

There is also no scanner. `CustodyService.ObserveAsync` is the entry point one
would call, and nothing calls it outside tests. Withdrawals are not here at all
— phase 3 of the wallet plan.

It publishes `deposit.credited.v1`. **Nothing consumes it.**

---

## Treasury — works, and is the console's whole backend for now

All under `/v1/admin/treasury`. Reading needs `treasury.read`; a credit is
proposed with `treasury.propose` (`treasury-operator`) and posted only when
somebody else approves it with `treasury.approve` (`treasury-approver`):

- `GET /balances` — every account IslaPay holds in its own name: fees, the
  settlement fund, escrow, the float, and the mirrors of what is held outside.
  Each carries how many postings have touched it, which is what makes the
  figure checkable rather than merely displayable.
- `GET /reconciliation` — escrow, as the ledger has it against what the
  modules say it should be, per currency, with the per-context breakdown.
- `GET /accounts/{owner}/{currency}/entries` — one account's history, newest
  first. Mirrors are reached as `external:tron`, `external:bank:bandec`.
- `POST /credits` — the one door money enters by, and now only a proposal:
  it answers `201` with a `TreasuryProposalDto` and moves nothing. Requires an
  `Idempotency-Key`, a destination (`float` or `settlement_fund` — not escrow,
  not fees), a source mirror and a reason, all checked when proposed and again
  when approved; the author comes from the token and never from the body.
- `GET /proposals?status=`, `GET /proposals/{id}` — what is waiting and what
  was decided.
- `POST /proposals/{id}/approve` — posts the credit. Refused to whoever
  proposed it (`own_proposal`), whatever roles they hold; idempotent, and the
  posting is keyed by the proposal, so a retry never posts twice. The ledger
  entry carries `by` (proposer) and `approved_by`.
- `POST /proposals/{id}/reject` (with a note), `POST /proposals/{id}/withdraw`
  (proposer only). A proposal nobody decides expires after 24 hours.

**Owns no schema and no money**, which is the design rather than an omission.
A treasury that kept its own figures would be a second set of books, and the
second set is always the one that is wrong. Everything it reports it asks
somebody else for.

**The reconciliation is a band, not an equality.** A module and the ledger
commit separately, so at any instant some money is mid-movement. Each module
reports what it is certain of and what is in flight, through
`IEscrowReporter`; the ledger's escrow is expected to sit between the two.
Inside with nothing in flight is agreement, inside with something in flight is
"ask again in a moment", and outside is worth waking somebody for. Counting
in-flight money either way produces alarms that are not real, and an alarm
that cries wolf is the one people learn to dismiss.

`MarketplaceEscrowReporter` and `P2PEscrowReporter` exist and are registered.
Storefront's does not, because Storefront does not.

**What is missing.** There is no way out: a credit can be reversed only by
posting its inverse, which no route does. No CSV or statement export. The
balance is not checked against the sum of its own entries — `entry_count` is
reported so the console can watch it, but nothing recomputes the fold. And no
console: that is a separate repository, and this is what it will read.

The 16 module tests run against a real Postgres, and 5 end-to-end tests run
against the host — including one that reconciles escrow against a hold placed
by a real buyer on a real listing, so what is being compared is a module's
reporter and the ledger's balance rather than two numbers a test wrote.

---

## Staff roles and permissions — works

Routes ask for a **permission**; a **role** is a job, granted in Keycloak,
that carries a set of them. The whole table is one file,
`Platform.AspNet/Security/StaffRoles.cs`, so separation of duties can be read
and tested in one place.

| Role | Permissions |
|---|---|
| `support` | `support.read`, `p2p.read` |
| `p2p-operator` | `p2p.read`, `p2p.settle` |
| `p2p-manager` | `p2p.read`, `p2p.manage` |
| `treasury-operator` | `treasury.read`, `treasury.propose` |
| `treasury-approver` | `treasury.read`, `treasury.approve` |
| `compliance` | `support.read`, `compliance.read`, `compliance.act`, `audit.read` |
| `catalog-admin` | `catalog.manage` |
| `security-admin` | `security.manage`, `audit.read` |
| `auditor` | every `.read`, nothing else |

- **Conflicts.** `treasury-operator` + `treasury-approver`, and
  `security-admin` with any role that moves money. A token carrying both halves
  of a pair gets neither half's permissions, and says so.
- **Second factor.** With `Security:RequireMultiFactor` (on by default, off in
  Development) a staff role counts only when the token's `amr` says `otp`.
  `POST /v1/auth/login` takes an optional `code` for this.
- **`GET /v1/me/permissions`** — roles, permissions, conflicts and whether a
  second factor is missing. The console draws its navigation from it; every
  route still checks for itself.
- Keycloak's `realm_access` is read in one place, the Identity module
  (`RealmRoleClaims`), instead of three copies in three modules.
- An end-to-end test fails if any `/v1/admin` route lacks a permission.
- **Audit log** (`platform.audit_log`). Every non-GET call to a route behind a
  permission is recorded however it ends — who, which permission, the route,
  its ids, the status — because `RequirePermission` attaches the filter that
  writes it; a route cannot be protected without being audited. Refusals of
  those routes are recorded too. Treasury adds its own rows for proposals
  (amount, destination, proposer). Append-only by trigger (UPDATE, DELETE and
  TRUNCATE raise), and hash-chained: each row's hash covers the previous one.
  Read with `GET /v1/admin/audit?actor=&action=&cursor=` (`audit.read`).
- `tools/provision-realm.py` creates the nine roles, turns on brute-force
  protection and names the sign-in steps so `amr` is written.

`treasury-admin` is gone: it was one role that could both fund the float and
check it. Whoever held it needs `treasury-operator` now, and the credit becomes
a proposal someone else approves when approvals land.

---

## Platform

**The published specification.** `GET /openapi/v1.json`, unauthenticated, and
on by default only in Development — the routes are discoverable by anyone with
a token, so this is not a secret, but a complete map of the admin surface
handed to anonymous callers is a convenience for an attacker and for nobody
else. `OpenApi:Enabled` turns it on where a build machine needs it.

It exists because the admin console is a separate repository that will never
link against these assemblies: either it generates its client from this
document or somebody transcribes the shapes from a response, and a
transcription drifts silently. Two things reflection gets wrong are corrected
by hand — `Money`, whose .NET shape is nothing like the object it travels as,
and the bearer scheme, which no endpoint declares on its own — and every
operation carries one `default` response describing `ApiProblem`, because the
platform translates every module's refusal into the same body.

Four end-to-end tests hold it: one asserts every route is described, one that
`Money` is an `{amount, currency}` object, one that the bearer scheme is
there, and one that no operation answers an untyped 200 and no schema is
anonymous. That last one found four real gaps the moment it was written — two
health probes and two auth routes described as returning nothing at all — and
is what stops the next endpoint written as `Results.Ok(x)` from quietly
shipping a `void` to whoever generates a client.

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
`x.wallet`, `x.marketplace` and `x.p2p` are declared, receive their messages,
and drop them for want of a binding. Seven event types are published into
nothing:
`transfer.completed.v1`, `order.held.v1`, `order.released.v1`,
`order.refunded.v1`, `trade.committed.v1`, `trade.completed.v1`,
`trade.refunded.v1`, `deposit.credited.v1` — eight now.

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

## Compliance — the controls exist; the programme does not

**What there is now:**

- **Freeze.** `POST /v1/admin/compliance/accounts/{id}/freeze` and `/unfreeze`
  (`compliance.act`, reason required). The mark lives on the account in
  Keycloak beside the phone's, and Wallet, P2P, Marketplace and Custody read it
  on every movement through `IUserDirectory` — so it applies on the next
  request, not when the token expires. A frozen person can still sign in and
  see their money; `UserDto.frozen` tells the app. Deposits to an address the
  account already has are still credited (a chain cannot be refused); no new
  address is issued.
- **Levels.** 0 phone unproved, 1 phone proved, 2 identity checked by
  compliance (`PUT /v1/admin/compliance/accounts/{id}/level`). One movement may
  be at most 1,000 at level 1 and 25,000 at level 2, in units of the currency
  (every holdable currency is dollar-denominated today). Refused with
  `limit_exceeded` and `meta.limit`, `meta.currency`, `meta.level`. The rule is
  one function, `Standing.Check`, in Identity's contracts.
- **Lookup** by e-mail or id for support (`support.read`).
- Every change carries its reason into the audit log.

**What there is not:** daily or monthly totals (the ledger would have to sum a
person's outflows), sanctions screening, transaction monitoring, a
suspicious-activity workflow, document upload, and originator or beneficiary
data on a transfer (FATF R.16). Those are not a coding decision first — the
company's structure decides the architecture, not the other way round.

---

## Operations

No Dockerfile for the application. The `docker-compose.yml` brings up
dependencies for development, not the system. No tracing, no metrics, no
backups, no runbook, no HTTP rate limiting, no CORS policy. CI builds and tests
against real Postgres, RabbitMQ and Keycloak on every push, and that is the
whole of the automation.

---

## The client

Flutter, and `USE_MOCKS` still defaults to **true**.

**Authentication and the wallet** are wired to this backend for real.
Authentication has been for a while — token pair, keystore, pre-emptive
refresh, single-flight, one replay on a 401. The wallet is new:
`GET /v1/me/wallet` and `POST /v1/transfers` behind a `HttpWalletRepository`,
with the idempotency key sent as a header and kept across the refresh replay,
and this module's error codes mapped to the client's own failure types. The
rest of `WalletRepository` — convert, recharge, charge, the P2P trade, a
deposit address — throws by name rather than reaching an endpoint that is not
there, because a 404 arriving inside a payment flow reads to a user as "your
money did not go through".

**The client's wallet tests parse a response this backend really produced.**
`WireCaptureTests` writes one to disk when `WALLET_CAPTURE_PATH` is set, and
the file is checked into the Flutter repository. The first capture disagreed
with this repository twice — the rate keys above, and a transfer's `meta`,
which carries `to`, `toName`, `from`, `fromName` and `note` where
`LedgerEntryTypes` had documented a `destination` the server has never sent.
The client's history rendered "Enviado" with nobody's name in it. Both are
fixed; the fixture is why they were found.

**The dead code is gone.** 111 files and 20,733 lines of the pre-Riverpod
generation, which no longer compiled since the rework dropped `flutter_bloc`,
`get_it` and `equatable`, along with the 52 analyzer exclusions that hid them
and the three test files that had been failing against them. `ApiService` lost
fifteen methods aimed at endpoints that do not exist, and the mock backend
lost the `double`-arithmetic wallet, exchange, cards and notifications nothing
had called since money became an integer. `flutter analyze` is clean with no
exclusions at all and 76 tests pass.

---

## What makes this functional, in order

1. **A way in.** There are two now, and neither is finished for the same
   shape of reason: P2P buy needs somebody to work the queue, and an on-chain
   deposit needs a custodian behind `IDepositAddresses` and something reading
   blocks. The custody one is the shorter path — it needs no person in the
   loop once it is wired.
2. **Marketplace on the phone.** A QR scanner, and the five screens the flow
   needs.
3. **Somewhere to run.** A Dockerfile, and the whole thing up with one
   command.

Wallet on the phone is done, and deleting the dead Dart with it.

After that, and only after that, the question is which of Exchange, P2P,
recharge and payout comes first — and that answer depends on the company's
legal structure rather than on this repository.
