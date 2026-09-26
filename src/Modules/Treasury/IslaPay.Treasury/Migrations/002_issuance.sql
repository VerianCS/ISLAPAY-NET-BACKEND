-- Proposals for E-ISLA as well as for money in.
--
-- A mint takes E-ISLA from the issuer into a platform account, a burn takes it
-- back. Both move no outside money, and both are exactly as consequential as a
-- credit: whoever can mint alone can pay anybody anything. So they wait for a
-- second person in the same table. For a mint, `source` is 'issuer'; for a
-- burn, `destination` is.

ALTER TABLE treasury.proposals DROP CONSTRAINT proposals_kind_check;
ALTER TABLE treasury.proposals ADD CONSTRAINT proposals_kind_check
    CHECK (kind IN ('credit', 'mint', 'burn'));
