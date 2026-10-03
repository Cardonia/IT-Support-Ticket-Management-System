-- Level 32: ticket administration
-- Run once as the DB admin / table owner (NOT app_user), after db/005_admin.sql:
--   psql -h localhost -U <admin> -d it_db -f db/006_ticket_admin.sql
-- Idempotent: safe to run again. One transaction: either everything happens or nothing does.
--
-- What it adds
--   1. tickets.deleted_at / tickets.deleted_by   soft delete: a deleted ticket (and its notes) stays in the
--                                                 database, but every normal page and API hides it. There is no
--                                                 "restore" in the web area; a restore is a deliberate SQL act.
--   2. tickets_deleted_check                      deleted_at and deleted_by are set together or not at all.
--   3. tickets_state_check                        the ticket state machine as a database rule:
--                                                   Open        = no assignee, no resolved_at
--                                                   In Progress = assignee,    no resolved_at
--                                                   Resolved    = assignee,    resolved_at
--                                                 Added NOT VALID first (so it can never fail on old rows and
--                                                 break the migration), then validated ONLY if every existing row
--                                                 already satisfies it. If some rows do not, a NOTICE says how
--                                                 many and how to list them; the rule still applies to every new
--                                                 insert and every update from then on.
--   4. tickets_deleted_guard (trigger)            once a ticket is deleted, its deleted_at / deleted_by can not be
--                                                 changed or cleared by the application account (app_user), and
--                                                 not by anyone else unless the session sets
--                                                 app.admin_change = 'allowed' (the same switch as db/ADMIN_PROCEDURES.md).
--   5. one small index for the "deleted" filter.
-- The other indexes the admin pages need already exist since db/005_admin.sql.
-- No new grants are needed: app_user already has UPDATE on tickets.

BEGIN;

ALTER TABLE tickets ADD COLUMN IF NOT EXISTS deleted_at timestamptz;
ALTER TABLE tickets ADD COLUMN IF NOT EXISTS deleted_by bigint REFERENCES users(id);

-- both or neither (a deleted ticket always says who deleted it and when)
ALTER TABLE tickets DROP CONSTRAINT IF EXISTS tickets_deleted_check;
ALTER TABLE tickets ADD CONSTRAINT tickets_deleted_check
    CHECK ((deleted_at IS NULL) = (deleted_by IS NULL)) NOT VALID;
ALTER TABLE tickets VALIDATE CONSTRAINT tickets_deleted_check;      -- the columns are brand new: always true

-- the state machine; validated only when the existing data already obeys it
DO $$
DECLARE
    bad bigint;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint
                   WHERE conrelid = 'tickets'::regclass AND conname = 'tickets_state_check') THEN
        ALTER TABLE tickets ADD CONSTRAINT tickets_state_check CHECK (
            (status = 'Open'        AND assigned_to IS NULL     AND resolved_at IS NULL) OR
            (status = 'In Progress' AND assigned_to IS NOT NULL AND resolved_at IS NULL) OR
            (status = 'Resolved'    AND assigned_to IS NOT NULL AND resolved_at IS NOT NULL)
        ) NOT VALID;
    END IF;

    IF NOT (SELECT convalidated FROM pg_constraint
            WHERE conrelid = 'tickets'::regclass AND conname = 'tickets_state_check') THEN
        SELECT count(*) INTO bad FROM tickets
        WHERE NOT ((status = 'Open'        AND assigned_to IS NULL     AND resolved_at IS NULL) OR
                   (status = 'In Progress' AND assigned_to IS NOT NULL AND resolved_at IS NULL) OR
                   (status = 'Resolved'    AND assigned_to IS NOT NULL AND resolved_at IS NOT NULL));
        IF bad = 0 THEN
            ALTER TABLE tickets VALIDATE CONSTRAINT tickets_state_check;
        ELSE
            RAISE NOTICE 'tickets_state_check was added but NOT validated: % existing ticket(s) break it. List them with: SELECT id, status, assigned_to, resolved_at FROM tickets WHERE NOT ((status = ''Open'' AND assigned_to IS NULL AND resolved_at IS NULL) OR (status = ''In Progress'' AND assigned_to IS NOT NULL AND resolved_at IS NULL) OR (status = ''Resolved'' AND assigned_to IS NOT NULL AND resolved_at IS NOT NULL)); fix them, then run this file again to validate. The rule already applies to every new insert and update.', bad;
        END IF;
    END IF;
END
$$;

-- a deleted ticket stays deleted (for the application account always; for others only with the terminal switch)
CREATE OR REPLACE FUNCTION tickets_deleted_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.deleted_at IS NOT NULL
       AND (NEW.deleted_at IS DISTINCT FROM OLD.deleted_at OR NEW.deleted_by IS DISTINCT FROM OLD.deleted_by)
       AND (session_user = 'app_user'
            OR coalesce(current_setting('app.admin_change', true), '') <> 'allowed') THEN
        RAISE EXCEPTION 'A deleted ticket can only be restored from the database terminal (see db/ADMIN_PROCEDURES.md)'
            USING ERRCODE = '42501';
    END IF;
    RETURN NEW;
END
$$;

DROP TRIGGER IF EXISTS tickets_deleted_guard ON tickets;
CREATE TRIGGER tickets_deleted_guard
    BEFORE UPDATE ON tickets
    FOR EACH ROW EXECUTE FUNCTION tickets_deleted_guard();

-- "show deleted tickets" filter; the normal lists never look at deleted rows
CREATE INDEX IF NOT EXISTS tickets_deleted_idx ON tickets (deleted_at) WHERE deleted_at IS NOT NULL;

COMMIT;
