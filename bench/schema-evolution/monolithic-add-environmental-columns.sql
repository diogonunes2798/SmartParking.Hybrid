START TRANSACTION;
SET LOCAL lock_timeout = '5s';

ALTER TABLE monolith.occupancy_events
    ADD COLUMN temperature double precision,
    ADD COLUMN air_quality double precision,
    ADD COLUMN noise       double precision;

INSERT INTO __ef_migrations_history (migration_id, product_version)
VALUES ('20261116195745_AddEnvironmentalColumns', '10.0.15');

COMMIT;

