-- The least-privilege database login for the application.
-- Run as the DB owner (NOT as app_user), AFTER db/005_admin.sql:
--   psql -h localhost -U <owner> -d it_db -v app_password='CHOOSE-A-LONG-RANDOM-PASSWORD' -f db/app_user.sql
-- Idempotent. Safe to run again (it also resets the password to the value you pass).
--
-- IMPORTANT: this script does NOT change how the application connects. The application keeps using
-- the connection string in appsettings.json until YOU edit it. Recommended order:
--   1. run db/check_permissions.sql (read only) and look at it;
--   2. run this script;
--   3. run db/check_permissions.sql again: every row in section 5 must say ok;
--   4. put app_user into ConnectionStrings:Default, restart, click through the app once
--      (register, log in, create a ticket, take it, add a note, resolve it, log out);
--   5. if anything fails, put the old connection string back: nothing else changed.
--
-- What app_user may do is exactly the list of statements the application runs (ARCHITECTURE section 7.3),
-- plus the audit log and the few users columns the admin features need (Levels 29 to 31):
--   users          SELECT, INSERT; UPDATE only on first_name, role, is_active, password, must_change_password
--   sessions       SELECT, INSERT, DELETE
--   tickets        SELECT, INSERT, UPDATE            (no DELETE: tickets are never removed)
--   ticket_notes   SELECT, INSERT                    (notes are never changed or removed)
--   audit_log      SELECT, INSERT                    (history is append-only)
--   sequences      USAGE, SELECT
-- It can not create objects, can not become another role, and database triggers refuse it any change
-- to an Admin account (db/005_admin.sql).

\if :{?app_password}
\else
    \echo 'Missing password. Run with:  -v app_password=''...'''
    \quit
\endif

SELECT NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'app_user') AS need_create \gset

\if :need_create
    CREATE ROLE app_user LOGIN PASSWORD :'app_password'
        NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS INHERIT;
\else
    ALTER ROLE app_user LOGIN PASSWORD :'app_password'
        NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
\endif

BEGIN;

-- Start from nothing, then grant exactly the list above (removes any broader grant given earlier).
REVOKE ALL ON ALL TABLES    IN SCHEMA public FROM app_user;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM app_user;

SELECT format('GRANT CONNECT ON DATABASE %I TO app_user', current_database()) \gexec
GRANT USAGE ON SCHEMA public TO app_user;

GRANT SELECT, INSERT ON users TO app_user;
GRANT UPDATE (first_name, role, is_active, password, must_change_password) ON users TO app_user;

GRANT SELECT, INSERT, DELETE ON sessions TO app_user;
GRANT SELECT, INSERT, UPDATE ON tickets TO app_user;
GRANT SELECT, INSERT ON ticket_notes TO app_user;
GRANT SELECT, INSERT ON audit_log TO app_user;

-- Inserts need the identity sequences (tickets, ticket_notes, audit_log).
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO app_user;

COMMIT;

\echo 'app_user is ready. Now run db/check_permissions.sql and change the connection string yourself.'
