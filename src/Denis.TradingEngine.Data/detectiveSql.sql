
🔍 Šta ovaj upit radi?

✔ Bezbedno (ne vraća milione redova)
	•	raw_ticks i enriched su CTE koji filtrira vreme + simbole.
	•	Finalni SELECT vraća 23 redova — po jedan za svaki simbol (10 aktivnih + 13 test).

✔ Spread i micro-volatilnost
	•	Spread se računa iz (ask - bid) / mid.
	•	abs_tick_change koristi bid promenu jer nemaš last.

✔ Možeš odmah da vidiš:
	•	Ko je likvidan (mnogo tickova, mali spread)
	•	Ko je mrtav (malo tickova, veliki spread)
	•	Ko je divlji (veliki stddev, velike promene per tick)
	•	Ko je "kao NVDA" i možda treba da se doda ili ukloni iz liste

🔧 DEBUG: Ako neki simbol nema podataka, pokreni ovaj upit:
	SELECT symbol, COUNT(*) as tick_count, MIN(utc) as first_tick, MAX(utc) as last_tick, 
	       MIN(utc AT TIME ZONE 'UTC') as first_tick_utc, MAX(utc AT TIME ZONE 'UTC') as last_tick_utc
	FROM market_ticks
	WHERE symbol IN ('NVDA', 'BAC', 'TXN', 'WMT', 'KO', 'JNJ', 'PG', 'VZ', 'T', 'IBM', 'NKE', 'SBUX', 'MMM', 'GE', 'RTX')
	  AND DATE(utc AT TIME ZONE 'UTC') = CURRENT_DATE
	GROUP BY symbol
	ORDER BY tick_count DESC;



    WITH cfg_symbols AS (
    SELECT unnest(ARRAY[
        'QCOM','ORCL','NVDA','MS','BAC',
        'CSCO','INTC','XOM','DIS','PYPL',
        'TXN','WMT','KO','JNJ','PG',
        'VZ','T','IBM','NKE','SBUX',
        'MMM','GE','RTX'
    ]) AS symbol
),
raw_ticks AS (
    SELECT
        symbol,
        utc,
        bid,
        ask
    FROM market_ticks
    WHERE utc >= DATE_TRUNC('day', CURRENT_TIMESTAMP AT TIME ZONE 'UTC') + INTERVAL '13 hours 30 minutes'  -- 13:30 UTC = 14:30 CET
      AND utc <  DATE_TRUNC('day', CURRENT_TIMESTAMP AT TIME ZONE 'UTC') + INTERVAL '20 hours 5 minutes'   -- 20:05 UTC = 21:05 CET
      AND symbol IN (
        'QCOM','ORCL','NVDA','MS','BAC',
        'CSCO','INTC','XOM','DIS','PYPL',
        'TXN','WMT','KO','JNJ','PG',
        'VZ','T','IBM','NKE','SBUX',
        'MMM','GE','RTX'
      )
),
enriched AS (
    SELECT
        r.*,
        -- Spread u BPS
        (r.ask - r.bid)
            / NULLIF((r.ask + r.bid) / 2, 0) * 10000
            AS spread_bps,

        -- apsolutna promena BID-a po ticku (jer nemaš last)
        ABS(
            r.bid - LAG(r.bid) OVER (
                PARTITION BY r.symbol ORDER BY r.utc
            )
        ) AS abs_tick_change
    FROM raw_ticks r
)
SELECT
    cfg.symbol,

    COUNT(e.symbol)                      AS tick_count,
    MIN(e.utc)                           AS first_tick_utc,
    MAX(e.utc)                           AS last_tick_utc,
    EXTRACT(EPOCH FROM (MAX(e.utc) - MIN(e.utc))) / 60.0 AS duration_minutes,

    ROUND(AVG(e.spread_bps)::numeric, 2) AS avg_spread_bps,
    ROUND(
        PERCENTILE_CONT(0.5)
        WITHIN GROUP (ORDER BY e.spread_bps)::numeric, 2
    )                                    AS median_spread_bps,
    ROUND(MIN(e.spread_bps)::numeric, 2) AS min_spread_bps,
    ROUND(MAX(e.spread_bps)::numeric, 2) AS max_spread_bps,

    ROUND(STDDEV_POP(e.bid)::numeric, 6)            AS bid_stddev,
    ROUND(AVG(e.abs_tick_change)::numeric, 6)       AS avg_abs_tick_change,
    ROUND(MAX(e.abs_tick_change)::numeric, 6)       AS max_abs_tick_change,
    
    -- Ticks per minute (aktivnost)
    ROUND(
        COUNT(e.symbol)::numeric / NULLIF(EXTRACT(EPOCH FROM (MAX(e.utc) - MIN(e.utc))) / 60.0, 0),
        2
    ) AS ticks_per_minute

FROM cfg_symbols cfg
LEFT JOIN enriched e
    ON e.symbol = cfg.symbol
GROUP BY cfg.symbol
ORDER BY tick_count DESC NULLS LAST;