-- The transactional outbox (§7.3).
--
-- One table, owned by the platform, with a `context` column — not one table
-- per module. The outbox is infrastructure in the same sense the connection
-- pool is: modules write to it through IOutbox and never name it, so the
-- sharing is invisible to them, and splitting a context out later means moving
-- its rows rather than untangling a shared schema. The alternative, an
-- identical table copied into every module's schema, buys purity and pays for
-- it with N migrations and N pollers that must be kept in step.

CREATE SCHEMA IF NOT EXISTS messaging;

CREATE TABLE messaging.outbox (
    id              bigserial   PRIMARY KEY,
    -- The bounded context that produced it; becomes the exchange x.<context>.
    context         text        NOT NULL,
    routing_key     text        NOT NULL,
    payload         jsonb       NOT NULL,
    correlation_id  text,
    causation_id    text,
    occurred_at     timestamptz NOT NULL DEFAULT now(),
    published_at    timestamptz,
    attempts        integer     NOT NULL DEFAULT 0,
    last_error      text
);

-- Partial: the poller only ever asks for unpublished rows, and this keeps the
-- index the size of the backlog rather than the size of history.
CREATE INDEX outbox_pending ON messaging.outbox (id) WHERE published_at IS NULL;
