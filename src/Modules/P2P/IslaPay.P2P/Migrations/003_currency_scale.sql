-- A stored amount says what a unit is. See marketplace/002 for the reasoning,
-- and for why the scales are listed here rather than read from catalog.
-- P2P has more of them because every trade spans two currencies.
ALTER TABLE p2p.methods ADD COLUMN local_scale  int;
ALTER TABLE p2p.methods ADD COLUMN wallet_scale int;
ALTER TABLE p2p.rates   ADD COLUMN wallet_scale int;
ALTER TABLE p2p.trades  ADD COLUMN wallet_scale int;
ALTER TABLE p2p.trades  ADD COLUMN local_scale  int;

-- The historical codes, repeated per statement rather than held in a temp
-- table: the migrator's transaction handling is its own business, and a
-- script that depends on it is a script that breaks when it changes.
UPDATE p2p.methods SET local_scale  = v.scale
  FROM (VALUES ('EISLA', 2), ('USDT', 6), ('USDC', 6), ('CUP', 2)) AS v(code, scale)
 WHERE v.code = p2p.methods.local_currency;
UPDATE p2p.methods SET wallet_scale = v.scale
  FROM (VALUES ('EISLA', 2), ('USDT', 6), ('USDC', 6), ('CUP', 2)) AS v(code, scale)
 WHERE v.code = p2p.methods.wallet_currency;
UPDATE p2p.rates   SET wallet_scale = v.scale
  FROM (VALUES ('EISLA', 2), ('USDT', 6), ('USDC', 6), ('CUP', 2)) AS v(code, scale)
 WHERE v.code = p2p.rates.wallet_currency;
UPDATE p2p.trades  SET wallet_scale = v.scale
  FROM (VALUES ('EISLA', 2), ('USDT', 6), ('USDC', 6), ('CUP', 2)) AS v(code, scale)
 WHERE v.code = p2p.trades.wallet_currency;
UPDATE p2p.trades  SET local_scale  = v.scale
  FROM (VALUES ('EISLA', 2), ('USDT', 6), ('USDC', 6), ('CUP', 2)) AS v(code, scale)
 WHERE v.code = p2p.trades.local_currency;

DO $$
DECLARE orphans bigint;
BEGIN
    SELECT count(*) INTO orphans
      FROM (SELECT 1 FROM p2p.methods WHERE local_scale IS NULL OR wallet_scale IS NULL
            UNION ALL SELECT 1 FROM p2p.rates  WHERE wallet_scale IS NULL
            UNION ALL SELECT 1 FROM p2p.trades WHERE wallet_scale IS NULL
                                                  OR local_scale IS NULL) AS x;

    IF orphans > 0 THEN
        RAISE EXCEPTION '% P2P row(s) are in a currency with no known scale.', orphans;
    END IF;
END $$;

ALTER TABLE p2p.methods ALTER COLUMN local_scale  SET NOT NULL;
ALTER TABLE p2p.methods ALTER COLUMN wallet_scale SET NOT NULL;
ALTER TABLE p2p.rates   ALTER COLUMN wallet_scale SET NOT NULL;
ALTER TABLE p2p.trades  ALTER COLUMN wallet_scale SET NOT NULL;
ALTER TABLE p2p.trades  ALTER COLUMN local_scale  SET NOT NULL;
