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
  IslaPay.Platform.Messaging        RabbitMQ transport (no contracts, by rule)
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

## Current state

Identity is the only module with behaviour. Ledger is domain rules with no
host, no database and no bus — its invariants are worth stating independently
of where entries are stored, and its property tests need to run thousands of
sequences per second. Wallet, Exchange and P2P are contracts only: the shapes
are agreed in `API_CONTRACT.md`, nothing serves them yet.

Nothing publishes an event. `Platform.Messaging` can declare the §7 topology
and publish with confirms, and has integration tests against a real broker, but
no module calls it. The first real event — Identity publishing
`user.registered.v1` for Wallet to consume — is what will exercise outbox,
publication, quorum queue, consumer and idempotency end to end. Until then the
transport is a well-tested guess.
