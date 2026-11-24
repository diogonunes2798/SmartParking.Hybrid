-- Application role and its permissions (spec 001; RNF-06, AC-10).
-- Applied by the Initial migration, as the table owner, after the tables exist.
-- The Api never connects as the owner: it uses a LOGIN role that is a member of smartparking_app,
-- created by scripts/db-app-user.sh (development) and by the test fixture.

-- Roles are cluster-wide: another database of the same cluster may have created it already.
DO $$
BEGIN
    CREATE ROLE smartparking_app NOLOGIN;
EXCEPTION
    WHEN duplicate_object THEN NULL;
END
$$;

-- No DDL rights: USAGE only, never CREATE on the schema.
GRANT USAGE ON SCHEMA public TO smartparking_app;

-- Business tables of the three aggregates (Table 15).
GRANT SELECT, INSERT, UPDATE, DELETE ON
    operators,
    parking_facilities,
    facility_opening_hours,
    zones,
    parking_spots,
    sensors,
    tariffs,
    users,
    vehicles,
    payment_methods,
    reservations,
    parking_sessions,
    payments
TO smartparking_app;

-- Audit trail: append-only. No UPDATE, DELETE or TRUNCATE.
GRANT SELECT, INSERT ON audit_entries TO smartparking_app;

-- Outbox: the reader (F3/F4) marks rows as published, so UPDATE; never DELETE.
GRANT SELECT, INSERT, UPDATE ON outbox_messages TO smartparking_app;
