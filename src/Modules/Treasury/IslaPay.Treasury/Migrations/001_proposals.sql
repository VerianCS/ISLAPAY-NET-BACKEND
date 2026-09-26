-- Money one person asked to move, waiting for a second.
--
-- The treasury kept no table of its own because a copy of the figures is a
-- second set of books. This is not that: nothing here is a balance. A
-- proposal is a request, and the money moves in the ledger, once, when
-- somebody other than the proposer approves it.

CREATE SCHEMA IF NOT EXISTS treasury;

CREATE TABLE treasury.proposals (
    id                uuid        PRIMARY KEY,
    kind              text        NOT NULL CHECK (kind IN ('credit')),
    status            text        NOT NULL
                      CHECK (status IN ('pending', 'approved', 'rejected', 'withdrawn', 'expired')),
    destination       text        NOT NULL,
    currency          text        NOT NULL,
    amount_minor      bigint      NOT NULL CHECK (amount_minor > 0),
    source            text        NOT NULL,
    reason            text        NOT NULL,
    proposed_by       text        NOT NULL,
    proposed_by_name  text,
    -- The proposer's Idempotency-Key, so a retried proposal is one proposal.
    request_key       text        NOT NULL,
    proposed_at       timestamptz NOT NULL,
    expires_at        timestamptz NOT NULL,
    decided_by        text,
    decided_by_name   text,
    decided_at        timestamptz,
    decision_note     text,
    posting_id        uuid,
    UNIQUE (proposed_by, request_key),
    -- Stated in the schema as well as the code: a row where the same person
    -- proposed and decided is a row that should not exist, withdrawal aside.
    CHECK (decided_by IS NULL OR decided_by <> proposed_by OR status = 'withdrawn')
);

CREATE INDEX proposals_pending ON treasury.proposals (proposed_at) WHERE status = 'pending';
