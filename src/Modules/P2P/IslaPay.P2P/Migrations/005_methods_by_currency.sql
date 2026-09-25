-- A rail is a currency, not a channel.
--
-- The market used to model "CUP Transfermóvil" as a rail that traded one
-- wallet currency, with its limits in that wallet currency. Both were wrong
-- for how the desk works:
--
-- - The peso is one market. Whether a buyer pays through Transfermóvil or
--   EnZona, the desk receives the same CUP, so there is one CUP rail. Where to
--   pay is what the instructions say.
-- - The wallet side is whatever a customer holds (E-ISLA, USDT, USDC), each
--   with its own prices on the same rail. The rates table was already keyed
--   by wallet currency; the method's single wallet_currency is what goes.
-- - Limits are a fact about the local leg: the desk can send 60,000 pesos in
--   one transfer whichever wallet currency they were bought with.

-- Limits in the local currency. The old values were wallet minor units and
-- have no local meaning, so they are replaced rather than converted:
-- 500 to 60,000 CUP.
ALTER TABLE p2p.methods DROP COLUMN wallet_currency;
ALTER TABLE p2p.methods DROP COLUMN wallet_scale;

-- One CUP. The row is copied under its new id, everything that referenced the
-- old one is pointed at it, and the old one goes. Trades keep method_name as
-- it was when they were placed: history says what the person saw.
INSERT INTO p2p.methods
    (id, name, local_currency, local_scale, available, instructions,
     minimum_minor, maximum_minor, updated_at)
SELECT 'cup', 'CUP', local_currency, local_scale, available, instructions,
       50000, 6000000, now()
  FROM p2p.methods
 WHERE id = 'cup_transfermovil';

UPDATE p2p.rates  SET method_id = 'cup' WHERE method_id = 'cup_transfermovil';
UPDATE p2p.trades SET method_id = 'cup' WHERE method_id = 'cup_transfermovil';
DELETE FROM p2p.methods WHERE id = 'cup_transfermovil';

-- One rail per currency, so the rule above cannot quietly come undone.
ALTER TABLE p2p.methods
    ADD CONSTRAINT methods_one_per_currency UNIQUE (local_currency);

-- A side stops being offered by appending a row with no rate. Deleting the old
-- rows would lose when it stopped; the check still refuses zero and below.
ALTER TABLE p2p.rates ALTER COLUMN rate DROP NOT NULL;
