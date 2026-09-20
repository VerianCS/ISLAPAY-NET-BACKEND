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

One Postgres cluster, one schema per context: `identity`, `ledger`, `wallet`,
plus `platform` for the migration history and `messaging` for the outbox.
Nothing reads across a schema boundary, which is what makes "one database per
service" a connection string change later rather than a rewrite.

Migrations are plain `.sql` files embedded in the module that owns the schema,
applied in name order and recorded with a checksum — editing an applied script
is refused, because it leaves every existing environment on the old definition
and every new one on the new.

No ORM. A ledger's correctness lives in the isolation level and in which rows
are locked, in what order, and those have to be visible in the code that
depends on them.

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

Working end to end: registering a user creates the account in Keycloak, writes
`user.registered.v1` to the outbox, publishes it, and Wallet opens the ledger
accounts. `GET /v1/me/wallet` then returns balances, rates and history.

Because Keycloak owns the account and this database cannot join the transaction
that created it, that event can be lost. Wallet therefore also opens accounts
on first read: the event is the fast path, the read is the guarantee. Both are
tested, the second with the broker deliberately unreachable.

Exchange and P2P are contracts only — the shapes are agreed in
`API_CONTRACT.md`, nothing serves them yet. The rates in the wallet response
are parity placeholders until Exchange exists, and say so in the code.

No money moves yet: there is no transfer, conversion or payment endpoint. The
ledger can post — with idempotency, row locks and an append-only history
enforced by the database — but nothing calls it except its own tests.
