-- Idempotent requests (§11.1).
--
-- The client generates one key per user intent — one per press of Send — and
-- persists it, so a retry after the app restarts reuses the same key rather
-- than creating a second movement. This is the server half of that bargain.

CREATE TABLE platform.idempotency (
    -- Scoped to the caller. Two users could otherwise choose the same key and
    -- one would be served the other's response, which is a data leak that
    -- looks like a coincidence.
    scope            text        NOT NULL,
    key              text        NOT NULL,
    endpoint         text        NOT NULL,
    -- The same key with a different body is a client bug, not a retry, and it
    -- is answered differently. Hashed rather than stored: a request body may
    -- contain anything and this table is not the place for it.
    request_hash     text        NOT NULL,
    status           text        NOT NULL CHECK (status IN ('in_flight', 'completed')),
    response_status  integer,
    response_body    text,
    started_at       timestamptz NOT NULL DEFAULT now(),
    completed_at     timestamptz,
    PRIMARY KEY (scope, key)
);

-- Rows are kept so a late retry still replays rather than re-executing; a
-- sweeper retires them once the client could no longer sensibly retry.
CREATE INDEX idempotency_by_age ON platform.idempotency (started_at);
