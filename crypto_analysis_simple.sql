-- ============================================================
-- JEDNOSTAVAN UPIT ZA ANALIZU DERIBIT I BITFINEX
-- ============================================================
-- Pokreni ovaj upit i pošalji mi rezultate
-- Datum: 2026-01-05, od 16h sa prekidima

WITH ticks_with_spread AS (
    SELECT 
        exchange,
        symbol,
        utc,
        bid,
        ask,
        (bid + ask) / 2 AS mid_price,
        CASE 
            WHEN bid IS NOT NULL AND ask IS NOT NULL AND bid > 0 
            THEN (ask - bid) / ((ask + bid) / 2) * 10000
            ELSE NULL 
        END AS spread_bps
    FROM market_ticks
    WHERE exchange IN ('Deribit', 'Bitfinex')
      AND symbol IN ('BTCUSD', 'ETHUSD')
      AND utc >= '2026-01-05 16:00:00+00'  -- Od 16h UTC (17h CET)
      AND bid IS NOT NULL 
      AND ask IS NOT NULL
      AND bid > 0 
      AND ask > bid
),
ticks_with_volatility AS (
    SELECT 
        *,
        ABS(mid_price - LAG(mid_price) OVER (PARTITION BY exchange, symbol ORDER BY utc)) / NULLIF(mid_price, 0) AS price_change_fraction
    FROM ticks_with_spread
)
SELECT 
    exchange,
    symbol,
    
    -- Osnovne statistike
    COUNT(*) AS total_ticks,
    MIN(utc) AS first_tick,
    MAX(utc) AS last_tick,
    ROUND(EXTRACT(EPOCH FROM (MAX(utc) - MIN(utc))) / 3600, 2) AS hours_covered,
    
    -- SPREAD ANALIZA (za MaxSpreadBps)
    ROUND(AVG(spread_bps)::numeric, 2) AS avg_spread_bps,
    ROUND(PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY spread_bps)::numeric, 2) AS median_spread_bps,
    ROUND(PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY spread_bps)::numeric, 2) AS p95_spread_bps,
    ROUND(MAX(spread_bps)::numeric, 2) AS max_spread_bps,
    
    -- AKTIVNOST (za MinTicksPerWindow)
    ROUND(COUNT(*)::numeric / NULLIF(GREATEST(1, EXTRACT(EPOCH FROM (MAX(utc) - MIN(utc))) / 60), 0), 1) AS avg_ticks_per_minute,
    
    -- VOLATILNOST (za MinAtrFractionOfPrice)
    ROUND(AVG(price_change_fraction)::numeric, 8) AS avg_price_change_fraction,
    ROUND(PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY price_change_fraction)::numeric, 8) AS median_price_change_fraction,
    ROUND(PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY price_change_fraction)::numeric, 8) AS p95_price_change_fraction,
    
    -- PREPORUKE
    ROUND((PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY spread_bps) * 1.2)::numeric, 1) AS recommended_max_spread_bps,
    GREATEST(5, CAST(ROUND((COUNT(*)::numeric / NULLIF(GREATEST(1, EXTRACT(EPOCH FROM (MAX(utc) - MIN(utc))) / 60), 0)) * 0.3, 0) AS INTEGER)) AS recommended_min_ticks_per_window,
    ROUND(GREATEST(0.000005::numeric, (PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY price_change_fraction) * 0.5)::numeric), 8) AS recommended_min_atr_fraction_of_price

FROM ticks_with_volatility
WHERE spread_bps IS NOT NULL
GROUP BY exchange, symbol
ORDER BY exchange, symbol;

