-- Level 11: tickets table
-- Run once as the DB admin (NOT app_user):
--   psql -h localhost -U <admin> -d it_db -f db/003_tickets.sql
-- Idempotent: safe to run again. Needs db/migration.sql and db/002_roles.sql first (users table).
--
-- Status moves Open -> In Progress -> Resolved (enforced by the app in Levels 17-18).
-- assigned_to is the technician who took the ticket; resolved_at is set when it is resolved.

BEGIN;

CREATE TABLE IF NOT EXISTS tickets (
    id          bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    title       text        NOT NULL
                            CONSTRAINT tickets_title_check
                            CHECK (char_length(btrim(title)) BETWEEN 1 AND 100),
    description text        NOT NULL
                            CONSTRAINT tickets_description_check
                            CHECK (char_length(btrim(description)) BETWEEN 1 AND 2000),
    priority    text        NOT NULL DEFAULT 'Medium'
                            CONSTRAINT tickets_priority_check
                            CHECK (priority IN ('Low', 'Medium', 'High')),
    status      text        NOT NULL DEFAULT 'Open'
                            CONSTRAINT tickets_status_check
                            CHECK (status IN ('Open', 'In Progress', 'Resolved')),
    created_by  bigint      NOT NULL REFERENCES users(id),
    assigned_to bigint      REFERENCES users(id),
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    resolved_at timestamptz
);

-- "My tickets" (newest first) and the technician list filtered by status
CREATE INDEX IF NOT EXISTS tickets_created_by_idx ON tickets (created_by, created_at DESC);
CREATE INDEX IF NOT EXISTS tickets_status_idx     ON tickets (status, created_at DESC);

-- Grants for the limited app user. Skipped if that role does not exist (e.g. you still run as admin).
-- No DELETE: tickets are never deleted by the app.
-- The sequences grant is repeated on purpose: ALL SEQUENCES only covers sequences that already
-- exist, and this table just created tickets_id_seq.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'app_user') THEN
        GRANT SELECT, INSERT, UPDATE ON tickets TO app_user;
        GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO app_user;
    END IF;
END
$$;

COMMIT;
