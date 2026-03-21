-- ============================================================
-- ANALIZA CRYPTO EXCHANGE-OVA ZA PULLBACK FILTER PODEŠAVANJE
-- ============================================================
-- Ovaj fajl sadrži SQL upite za analizu Deribit i Bitfinex podataka
-- Koristi se za podešavanje pullback-config.json parametara
-- ============================================================

-- ============================================================
-- 1) MARKET TICKS ANALIZA - Spread, Volatilnost, Aktivnost
-- ============================================================
-- Pokazuje: spread statistike, tick aktivnost, volatilnost
-- Koristi se za: MaxSpreadBps, MinTicksPerWindow, MinAtrFractionOfPrice

WITH recent_ticks AS (
    SELECT 
        exchange,
        symbol,
        utc,
        bid,
        ask,
        CASE 
            WHEN bid IS NOT NULL AND ask IS NOT NULL AND bid > 0 
            THEN (ask - bid) / ((ask + bid) / 2) * 10000  -- Spread u BPS
            ELSE NULL 
        END AS spread_bps,
        CASE 
            WHEN bid IS NOT NULL AND ask IS NOT NULL 
            THEN (ask + bid) / 2  -- Mid price
            ELSE NULL 
        END AS mid_price
    FROM market_ticks
    WHERE exchange IN ('Deribit', 'Bitfinex')
      AND symbol IN ('BTCUSD', 'ETHUSD')
      AND utc >= NOW() - INTERVAL '24 hours'  -- Poslednja 24h
      AND bid IS NOT NULL 
      AND ask IS NOT NULL
      AND bid > 0 
      AND ask > bid
),
enriched AS (
    SELECT 
        exchange,
        symbol,
        utc,
        spread_bps,
        mid_price,
        LAG(mid_price) OVER (PARTITION BY exchange, symbol ORDER BY utc) AS prev_mid,
        EXTRACT(EPOCH FROM (utc - LAG(utc) OVER (PARTITION BY exchange, symbol ORDER BY utc))) AS seconds_since_prev
    FROM recent_ticks
    WHERE spread_bps IS NOT NULL
),
volatility AS (
    SELECT 
        exchange,
        symbol,
        ABS(mid_price - prev_mid) / NULLIF(prev_mid, 0) * 10000 AS price_change_bps
    FROM enriched
    WHERE prev_mid IS NOT NULL 
      AND prev_mid > 0
      AND seconds_since_prev <= 60  -- Samo tickove unutar 60s
)
SELECT 
    exchange,
    symbol,
    COUNT(*) AS total_ticks,
    COUNT(*) FILTER (WHERE utc >= NOW() - INTERVAL '1 hour') AS ticks_last_hour,
    COUNT(*) FILTER (WHERE utc >= NOW() - INTERVAL '10 minutes') AS ticks_last_10min,
    
    -- Spread statistike
    ROUND(AVG(spread_bps)::numeric, 2) AS avg_spread_bps,
    ROUND(PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY spread_bps)::numeric, 2) AS median_spread_bps,
    ROUND(PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY spread_bps)::numeric, 2) AS p95_spread_bps,
    ROUND(MIN(spread_bps)::numeric, 2) AS min_spread_bps,
    ROUND(MAX(spread_bps)::numeric, 2) AS max_spread_bps,
    
    -- Volatilnost (price change per tick)
    ROUND(AVG(price_change_bps)::numeric, 4) AS avg_price_change_bps,
    ROUND(STDDEV(price_change_bps)::numeric, 4) AS stddev_price_change_bps,
    ROUND(PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY price_change_bps)::numeric, 4) AS p95_price_change_bps,
    
    -- Aktivnost (ticks per minute)
    ROUND(COUNT(*)::numeric / NULLIF(EXTRACT(EPOCH FROM (MAX(utc) - MIN(utc))) / 60, 0), 2) AS ticks_per_minute,
    
    -- Vremenski opseg
    MIN(utc) AS first_tick,
    MAX(utc) AS last_tick
    
FROM enriched
LEFT JOIN volatility USING (exchange, symbol, utc)
GROUP BY exchange, symbol
ORDER BY exchange, symbol;

-- ============================================================
-- 2) SIGNAL SLAYER DECISIONS ANALIZA
-- ============================================================
-- Pokazuje: zašto su signali odbijeni/prihvaćeni
-- Koristi se za: razumevanje filter logike i podešavanje pragova

SELECT 
    exchange,
    symbol,
    accepted,
    reason_code,
    COUNT(*) AS decision_count,
    ROUND(AVG(spread_bps)::numeric, 2) AS avg_spread_bps,
    ROUND(AVG(activity_ticks)::numeric, 1) AS avg_activity_ticks,
    ROUND(AVG(atr_fraction)::numeric, 8) AS avg_atr_fraction,
    ROUND(AVG(price)::numeric, 2) AS avg_price,
    MIN(utc) AS first_decision,
    MAX(utc) AS last_decision
FROM signal_slayer_decisions
WHERE exchange IN ('Deribit', 'Bitfinex')
  AND symbol IN ('BTCUSD', 'ETHUSD')
  AND utc >= NOW() - INTERVAL '24 hours'
GROUP BY exchange, symbol, accepted, reason_code
ORDER BY exchange, symbol, accepted DESC, decision_count DESC;

-- ============================================================
-- 3) TRADE SIGNALS ANALIZA - Prihvaćeni vs Odbijeni
-- ============================================================
-- Pokazuje: koliko signala je generisano, koliko prihvaćeno
-- Koristi se za: razumevanje signal kvaliteta

SELECT 
    exchange,
    symbol,
    accepted,
    COUNT(*) AS signal_count,
    COUNT(*) FILTER (WHERE utc >= NOW() - INTERVAL '1 hour') AS signals_last_hour,
    ROUND(AVG(suggested_price)::numeric, 2) AS avg_suggested_price,
    STRING_AGG(DISTINCT reject_reason, ', ') AS reject_reasons,
    MIN(utc) AS first_signal,
    MAX(utc) AS last_signal
FROM trade_signals
WHERE exchange IN ('Deribit', 'Bitfinex')
  AND symbol IN ('BTCUSD', 'ETHUSD')
  AND utc >= NOW() - INTERVAL '24 hours'
GROUP BY exchange, symbol, accepted
ORDER BY exchange, symbol, accepted DESC;

-- ============================================================
-- 4) SPREAD DISTRIBUCIJA PO VREMENSKOM PERIODU
-- ============================================================
-- Pokazuje: kako se spread menja tokom dana
-- Koristi se za: MaxSpreadBps podešavanje

SELECT 
    exchange,
    symbol,
    DATE_TRUNC('hour', utc) AS hour_utc,
    COUNT(*) AS tick_count,
    ROUND(AVG(spread_bps)::numeric, 2) AS avg_spread_bps,
    ROUND(PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY spread_bps)::numeric, 2) AS median_spread_bps,
    ROUND(PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY spread_bps)::numeric, 2) AS p95_spread_bps,
    ROUND(MAX(spread_bps)::numeric, 2) AS max_spread_bps
FROM (
    SELECT 
        exchange,
        symbol,
        utc,
        CASE 
            WHEN bid IS NOT NULL AND ask IS NOT NULL AND bid > 0 
            THEN (ask - bid) / ((ask + bid) / 2) * 10000
            ELSE NULL 
        END AS spread_bps
    FROM market_ticks
    WHERE exchange IN ('Deribit', 'Bitfinex')
      AND symbol IN ('BTCUSD', 'ETHUSD')
      AND utc >= NOW() - INTERVAL '24 hours'
      AND bid IS NOT NULL 
      AND ask IS NOT NULL
      AND bid > 0 
      AND ask > bid
) AS spreads
WHERE spread_bps IS NOT NULL
GROUP BY exchange, symbol, DATE_TRUNC('hour', utc)
ORDER BY exchange, symbol, hour_utc DESC;

-- ============================================================
-- 5) AKTIVNOST ANALIZA - Ticks per Window (60s)
-- ============================================================
-- Pokazuje: koliko tickova ima u 60s prozoru
-- Koristi se za: MinTicksPerWindow podešavanje

WITH tick_windows AS (
    SELECT 
        exchange,
        symbol,
        utc,
        COUNT(*) OVER (
            PARTITION BY exchange, symbol 
            ORDER BY utc 
            RANGE BETWEEN INTERVAL '60 seconds' PRECEDING AND CURRENT ROW
        ) AS ticks_in_60s_window
    FROM market_ticks
    WHERE exchange IN ('Deribit', 'Bitfinex')
      AND symbol IN ('BTCUSD', 'ETHUSD')
      AND utc >= NOW() - INTERVAL '24 hours'
)
SELECT 
    exchange,
    symbol,
    COUNT(*) AS total_windows,
    ROUND(AVG(ticks_in_60s_window)::numeric, 1) AS avg_ticks_per_60s,
    ROUND(PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY ticks_in_60s_window)::numeric, 1) AS median_ticks_per_60s,
    ROUND(PERCENTILE_CONT(0.25) WITHIN GROUP (ORDER BY ticks_in_60s_window)::numeric, 1) AS p25_ticks_per_60s,
    ROUND(PERCENTILE_CONT(0.10) WITHIN GROUP (ORDER BY ticks_in_60s_window)::numeric, 1) AS p10_ticks_per_60s,
    MIN(ticks_in_60s_window) AS min_ticks_per_60s,
    MAX(ticks_in_60s_window) AS max_ticks_per_60s,
    COUNT(*) FILTER (WHERE ticks_in_60s_window >= 10) AS windows_with_10plus_ticks,
    COUNT(*) FILTER (WHERE ticks_in_60s_window >= 20) AS windows_with_20plus_ticks,
    ROUND(100.0 * COUNT(*) FILTER (WHERE ticks_in_60s_window >= 10) / NULLIF(COUNT(*), 0), 1) AS pct_windows_10plus,
    ROUND(100.0 * COUNT(*) FILTER (WHERE ticks_in_60s_window >= 20) / NULLIF(COUNT(*), 0), 1) AS pct_windows_20plus
FROM tick_windows
GROUP BY exchange, symbol
ORDER BY exchange, symbol;

-- ============================================================
-- 6) ATR PROXY ANALIZA - Volatilnost za MinAtrFractionOfPrice
-- ============================================================
-- Pokazuje: kolika je volatilnost (ATR proxy) u odnosu na cenu
-- Koristi se za: MinAtrFractionOfPrice podešavanje

WITH price_changes AS (
    SELECT 
        exchange,
        symbol,
        utc,
        (bid + ask) / 2 AS mid_price,
        ABS((bid + ask) / 2 - LAG((bid + ask) / 2) OVER (PARTITION BY exchange, symbol ORDER BY utc)) AS price_change,
        (bid + ask) / 2 AS current_price
    FROM market_ticks
    WHERE exchange IN ('Deribit', 'Bitfinex')
      AND symbol IN ('BTCUSD', 'ETHUSD')
      AND utc >= NOW() - INTERVAL '24 hours'
      AND bid IS NOT NULL 
      AND ask IS NOT NULL
      AND bid > 0
),
atr_proxy AS (
    SELECT 
        exchange,
        symbol,
        utc,
        current_price,
        price_change,
        AVG(price_change) OVER (
            PARTITION BY exchange, symbol 
            ORDER BY utc 
            ROWS BETWEEN 13 PRECEDING AND CURRENT ROW
        ) AS atr_proxy_14,
        price_change / NULLIF(current_price, 0) AS price_change_fraction
    FROM price_changes
    WHERE price_change IS NOT NULL
)
SELECT 
    exchange,
    symbol,
    COUNT(*) AS samples,
    ROUND(AVG(current_price)::numeric, 2) AS avg_price,
    ROUND(AVG(atr_proxy_14)::numeric, 6) AS avg_atr_proxy,
    ROUND(AVG(price_change_fraction)::numeric, 8) AS avg_price_change_fraction,
    ROUND(PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY price_change_fraction)::numeric, 8) AS median_price_change_fraction,
    ROUND(PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY price_change_fraction)::numeric, 8) AS p95_price_change_fraction,
    ROUND(AVG(atr_proxy_14 / NULLIF(current_price, 0))::numeric, 8) AS avg_atr_fraction_of_price,
    ROUND(PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY atr_proxy_14 / NULLIF(current_price, 0))::numeric, 8) AS median_atr_fraction_of_price
FROM atr_proxy
WHERE atr_proxy_14 IS NOT NULL
GROUP BY exchange, symbol
ORDER BY exchange, symbol;

-- ============================================================
-- 7) PREPORUKE ZA PULLBACK CONFIG (Summary)
-- ============================================================
-- Agregirana analiza sa preporukama

WITH spread_stats AS (
    SELECT 
        exchange,
        symbol,
        PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY 
            CASE 
                WHEN bid IS NOT NULL AND ask IS NOT NULL AND bid > 0 
                THEN (ask - bid) / ((ask + bid) / 2) * 10000
                ELSE NULL 
            END
        ) AS p95_spread_bps
    FROM market_ticks
    WHERE exchange IN ('Deribit', 'Bitfinex')
      AND symbol IN ('BTCUSD', 'ETHUSD')
      AND utc >= NOW() - INTERVAL '24 hours'
      AND bid IS NOT NULL 
      AND ask IS NOT NULL
    GROUP BY exchange, symbol
),
activity_stats AS (
    SELECT 
        exchange,
        symbol,
        PERCENTILE_CONT(0.25) WITHIN GROUP (ORDER BY ticks_in_60s) AS p25_ticks_per_60s
    FROM (
        SELECT 
            exchange,
            symbol,
            COUNT(*) OVER (
                PARTITION BY exchange, symbol 
                ORDER BY utc 
                RANGE BETWEEN INTERVAL '60 seconds' PRECEDING AND CURRENT ROW
            ) AS ticks_in_60s
        FROM market_ticks
        WHERE exchange IN ('Deribit', 'Bitfinex')
          AND symbol IN ('BTCUSD', 'ETHUSD')
          AND utc >= NOW() - INTERVAL '24 hours'
    ) AS windows
    GROUP BY exchange, symbol
),
atr_stats AS (
    SELECT 
        exchange,
        symbol,
        PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY atr_fraction) AS median_atr_fraction
    FROM (
        SELECT 
            exchange,
            symbol,
            ABS((bid + ask) / 2 - LAG((bid + ask) / 2) OVER (PARTITION BY exchange, symbol ORDER BY utc)) / NULLIF((bid + ask) / 2, 0) AS atr_fraction
        FROM market_ticks
        WHERE exchange IN ('Deribit', 'Bitfinex')
          AND symbol IN ('BTCUSD', 'ETHUSD')
          AND utc >= NOW() - INTERVAL '24 hours'
          AND bid IS NOT NULL 
          AND ask IS NOT NULL
    ) AS atr_calc
    WHERE atr_fraction IS NOT NULL
    GROUP BY exchange, symbol
)
SELECT 
    s.exchange,
    s.symbol,
    ROUND(s.p95_spread_bps::numeric, 1) AS recommended_max_spread_bps,
    GREATEST(5, ROUND(a.p25_ticks_per_60s::numeric, 0)::int) AS recommended_min_ticks_per_window,
    ROUND(GREATEST(0.000005, atr.median_atr_fraction * 0.5)::numeric, 8) AS recommended_min_atr_fraction_of_price
FROM spread_stats s
JOIN activity_stats a ON s.exchange = a.exchange AND s.symbol = a.symbol
JOIN atr_stats atr ON s.exchange = atr.exchange AND s.symbol = atr.symbol
ORDER BY s.exchange, s.symbol;

