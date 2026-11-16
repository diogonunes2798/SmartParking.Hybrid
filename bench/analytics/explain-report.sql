-- Phase 6 index review (ef-index-advisor, ADR-0010): plans of the two UC-04 report queries over a synthetic year of
-- hourly rows (50 facilities in 5 zones × 8 760 hours = 438 000 rows; D8 has about 5 000 spots).
-- Runs inside a transaction that is rolled back: the database is left as it was. VACUUM cannot run inside a
-- transaction, so the visibility map is not set and an index-only scan still shows heap fetches here.
-- Usage (from the repository root):
--   docker compose exec -T postgres psql -U smartparking -d smartparking -f - < bench/analytics/explain-report.sql
BEGIN;

INSERT INTO analytics.occupancy_hourly
    (facility_id, hour_start, zone, spot_count, occupied_seconds, available_seconds, event_count, aggregated_at)
SELECT f.id, h.hour_start, 'synthetic-' || (f.n % 5), 100, (random() * 360000)::bigint, 360000, (random() * 1200)::int, now()
FROM (SELECT n, gen_random_uuid() AS id FROM generate_series(1, 50) AS n) AS f
CROSS JOIN generate_series(timestamptz '2025-01-01 00:00Z', timestamptz '2025-12-31 23:00Z', interval '1 hour') AS h(hour_start);

ANALYZE analytics.occupancy_hourly;

\echo '--- Q1: one zone, granularity day, one month'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF)
SELECT zone, date_trunc('day', hour_start, 'Europe/Lisbon') AS period_start,
       count(DISTINCT hour_start)::int AS hours, sum(event_count)::bigint AS events,
       sum(occupied_seconds)::bigint AS occupied_seconds, sum(available_seconds)::bigint AS available_seconds
FROM analytics.occupancy_hourly
WHERE zone = 'synthetic-1' AND hour_start >= '2025-03-01 00:00Z' AND hour_start < '2025-04-01 00:00Z'
GROUP BY zone, 2
ORDER BY 2, zone;

\echo '--- Q2: every zone, granularity hour, one day'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF)
SELECT zone, date_trunc('hour', hour_start, 'Europe/Lisbon') AS period_start,
       count(DISTINCT hour_start)::int AS hours, sum(event_count)::bigint AS events,
       sum(occupied_seconds)::bigint AS occupied_seconds, sum(available_seconds)::bigint AS available_seconds
FROM analytics.occupancy_hourly
WHERE hour_start >= '2025-03-10 00:00Z' AND hour_start < '2025-03-11 00:00Z'
GROUP BY zone, 2
ORDER BY 2, zone;

\echo '--- Q3: every zone, granularity month, one year'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF)
SELECT zone, date_trunc('month', hour_start, 'Europe/Lisbon') AS period_start,
       count(DISTINCT hour_start)::int AS hours, sum(event_count)::bigint AS events,
       sum(occupied_seconds)::bigint AS occupied_seconds, sum(available_seconds)::bigint AS available_seconds
FROM analytics.occupancy_hourly
WHERE hour_start >= '2025-01-01 00:00Z' AND hour_start < '2026-01-01 00:00Z'
GROUP BY zone, 2
ORDER BY 2, zone;

ROLLBACK;
