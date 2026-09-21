# Architecture

A **modular monolith**: one deployable process, several bounded contexts with
real boundaries between them.

Not microservices, deliberately. A microservice buys independent deployment
and pays for it with a network between every call, distributed transactions,
and an operational burden a one-person team does not have. What actually makes
later extraction possible is not separate processes — it is no shared mutable
state, no shared types between contexts, integration by event rather than by
method call, and one schema per context. All four are free to do inside one
process and very expensive to retrofit, so they are done here.

## Layout

```
src/Platform/          technical, knows no business
  IslaPay.Platform.Money            money as integer minor units
  IslaPay.Platform.Serialization    the wire format, and only that
  IslaPay.Platform.Api              ApiProblem, CursorPage, platform error codes
  IslaPay.Platform.Data             Npgsql, transactions, SQL migrations
  IslaPay.Platform.Messaging        RabbitMQ transport + outbox (no contracts, by rule)
  IslaPay.Platform.AspNet           module contract, shared pipeline, health

src/Modules/<Context>/
  IslaPay.<Context>.Contracts       the module's public face — DTOs, codes, events
  IslaPay.<Context>                 its internals: domain, infrastructure, routes

src/IslaPay.Host/      the composition root, and nothing else
```

## Rules

1. The platform never references a module.
2. A module reaches another module only through that module's `.Contracts`.
3. Only the host composes modules; nothing references the host.
4. `Platform.Messaging` references no contracts — a transport must be able to
   carry an event without knowing what events exist.
5. `Platform.Api` references nothing at all, so it cannot become the shared
   kernel through which two modules accidentally meet.
6. A module's tests stay inside that module.

These are not conventions. `tests/Architecture/IslaPay.Architecture.Tests`
reads the repository's `.csproj` graph and fails the build on any violation,
naming the project and the reference to remove. A seam costs one line to
cross, so a rule that lives only in a document has already been broken
somewhere nobody has looked.

## Adding a module

Create `src/Modules/<Context>/IslaPay.<Context>.Contracts` and
`IslaPay.<Context>`, implement `IIslaPayModule` in the latter, and add one line
to `IslaPay.Host`. The module brings its own configuration section, services,
routes and readiness check; the host learns nothing about it beyond the type
name.

## Data

One Postgres cluster, one schema per context: `identity`, `ledger`,
`marketplace`, `p2p`, plus `platform` for the migration history and `messaging`
for the outbox. Wallet owns no tables; its state is the ledger's. Nothing reads
across a schema boundary, which is what makes "one database per service" a
connection string change later rather than a rewrite.

Migrations are plain `.sql` files embedded in the module that owns the schema,
applied in name order and recorded with a checksum — editing an applied script
is refused, because it leaves every existing environment on the old definition
and every new one on the new. **This is true of every module, including the
ones that use an ORM**: the schema is written by hand, and `dotnet ef` is not
part of any workflow here.

Access to those tables is split, deliberately:

- **The ledger, the outbox and the idempotency store use Npgsql directly.**
  Their correctness lives in the isolation level and in which rows are locked,
  in what order — `FOR UPDATE` in a fixed sequence, `FOR UPDATE SKIP LOCKED`,
  gapless per-account sequences, append-only triggers. Those have to be visible
  in the code that depends on them, and an ORM's job is to hide them.
- **Product modules use EF Core as a mapper.** Marketplace is the first.
  Listings, orders, trades, paging and projections are row-to-object tedium
  with no concurrency subtlety of their own, and there are more modules like
  them coming. Writing that by hand is several hundred lines per module that nobody
  will read twice.

A module that needs both does what Marketplace does: EF for the queries, and
raw SQL through EF's `FromSql`/`ExecuteSql` for the statements where the lock
is the point. The lock stays in the file you are reading.

## Events

Modules integrate by event, not by calling each other. A producer writes to
`messaging.outbox` — in the same transaction as the data that caused the event,
when it has one — and a poller publishes to RabbitMQ afterwards. That avoids
the dual write, and it makes delivery **at least once**: every consumer here is
idempotent, and that is a requirement rather than a nicety.

The one synchronous cross-module call is Wallet reading the ledger through
`ILedger`. It goes through Ledger's `.Contracts` and mentions no domain type,
so it is the same surface a network API would have.

## Current state

Working end to end:

- **Identity.** Register, sign in, refresh, sign out, prove a phone by OTP,
  reset a password — against a real Keycloak. Registration writes
  `user.registered.v1` to the outbox and Wallet opens the ledger accounts.
  Because Keycloak owns the account and this database cannot join the
  transaction that created it, that event can be lost, so Wallet also opens
  accounts on first read: the event is the fast path, the read is the
  guarantee. Both are tested, the second with the broker unreachable.
- **Wallet.** `GET /v1/me/wallet`, `GET /v1/me/transactions`, and
  `POST /v1/transfers` between two IslaPay accounts, with HTTP idempotency and
  the verified-phone gate.
- **Marketplace.** Publish an item, browse, and buy: the buyer's money moves
  into escrow and a single-use code is issued to them. The seller scans it and
  escrow pays out — the price less 1% commission. Either party can cancel, and
  a hold nobody scans returns on its own after 72 hours.
- **P2P.** Instant exchange against IslaPay between the wallet and Cuban
  pesos. A sell debits the wallet and owes the pesos through escrow until an
  operator confirms the transfer; a buy posts nothing until an operator
  confirms the pesos arrived. Both legs are in the ledger, which is why
  `Currency.Cup` exists — an obligation to send somebody money is a liability,
  not a column.

The marketplace is where the seam between a module's tables and the ledger's
transaction had to be faced. The ledger owns its transaction and will not hand
it out, so an order's status and the posting that moved its money are two
commits. The order lifecycle therefore has in-flight statuses (`pending`,
`releasing`, `refunding`) that say "a posting for this may exist", a sweeper
that settles the question with `ILedger.FindPostingAsync`, and one idempotency
key shared by the two ways an order can end, so that releasing to the seller
and refunding to the buyer compete for the same unique index. Escrow is a
platform account and may go negative: a second payout would not bounce, so
nothing may depend on it not being attempted.

Exchange and P2P are contracts only — the shapes are agreed in
`API_CONTRACT.md`, nothing serves them yet. The rates in the wallet response
are parity placeholders until Exchange exists, and say so in the code.

Money can now get in and out, through P2P — but only as fast as a person
works the queue, because the Cuban leg is a human sending a transfer. There is
no console for them, no Transfermóvil integration, and no endpoint that funds
the desk's pesos. Those are the next things that matter, and the first two are
more product than code.
