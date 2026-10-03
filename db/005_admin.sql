-- Level 26: Admin role, audit log and the database-level Admin protections
-- Run once as the DB owner (NOT app_user):
--   psql -h localhost -U <owner> -d it_db -f db/005_admin.sql
-- Idempotent: safe to run again. Needs db/002_roles.sql, 003_tickets.sql and 004_notes.sql first.
--
-- What this does (see ADMIN_PLAN.md sections 3.1 and 3.2):
--   1. users: the role 'Admin' becomes valid; new columns created_at, is_active, must_change_password.
--   2. audit_log: an append-only table (no update, no delete, no truncate, even for the owner,
--      unless the triggers are deliberately disabled).
--   3. Three triggers on users that protect Admin accounts INSIDE the database:
--        users_admin_guard     an Admin can only be created, changed or removed from the database
--                              terminal (SET LOCAL app.admin_change = 'allowed'), never by app_user
--        users_keep_one_admin  the last active Admin cannot be demoted, deactivated or deleted
--                              (checked at COMMIT, serialized with an advisory lock)
--        users_audit           role, active-state and name changes are written to audit_log by the
--                              database itself, so a change made in the terminal is recorded too
--   4. Indexes the later admin pages need.
--   5. If the role app_user exists: the few extra grants this level needs (db/app_user.sql sets up the
--      complete least-privilege role).
--
-- This migration changes no application behavior: the app does not use any of it yet.
-- Creating the first Admin is a manual step, see db/ADMIN_PROCEDURES.md.

BEGIN;

-- ---------------------------------------------------------------------------------------------
-- 1) users
-- ---------------------------------------------------------------------------------------------

-- 'Admin' joins the two existing roles. (If this fails, some row has another role value; look with
--   SELECT id, first_name, role FROM users WHERE role NOT IN ('Employee','Technician','Admin');
-- fix those rows and run this file again: the transaction rolled back, nothing changed.)
ALTER TABLE users DROP CONSTRAINT IF EXISTS users_role_check;
ALTER TABLE users ADD CONSTRAINT users_role_check
    CHECK (role IN ('Employee', 'Technician', 'Admin'));

-- Registration date. Added WITHOUT a default first, so users that already exist stay NULL
-- ("before tracking") instead of all getting today's date; then the default is set for new users.
ALTER TABLE users ADD COLUMN IF NOT EXISTS created_at timestamptz;
ALTER TABLE users ALTER COLUMN created_at SET DEFAULT now();

-- Deactivation instead of deletion: tickets, notes and audit history keep their meaning.
ALTER TABLE users ADD COLUMN IF NOT EXISTS is_active boolean NOT NULL DEFAULT true;

-- Set when an admin creates the account or resets the password (used from Level 30 on).
ALTER TABLE users ADD COLUMN IF NOT EXISTS must_change_password boolean NOT NULL DEFAULT false;

CREATE INDEX IF NOT EXISTS users_role_idx       ON users (role);
CREATE INDEX IF NOT EXISTS users_created_at_idx ON users (created_at);

-- ---------------------------------------------------------------------------------------------
-- 2) audit_log (append-only)
-- ---------------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS audit_log (
    id          bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    at          timestamptz NOT NULL DEFAULT now(),
    -- NOT a foreign key on purpose: history must survive any change to the users table.
    -- NULL actor = the database terminal / the system.
    actor_id    bigint,
    actor_name  text,                 -- a copy, so history stays readable
    actor_role  text,                 -- the actor's role at that moment
    action      text        NOT NULL
                            CONSTRAINT audit_log_action_check CHECK (action IN (
                                'user.registered', 'auth.login', 'auth.logout', 'auth.login_failed',
                                'user.password_changed',
                                'ticket.created', 'ticket.taken', 'ticket.resolved', 'ticket.released',
                                'note.added',
                                'admin.user_created', 'admin.user_updated', 'admin.role_changed',
                                'admin.user_deactivated', 'admin.user_reactivated', 'admin.password_reset',
                                'admin.ticket_created', 'admin.ticket_updated', 'admin.ticket_assigned',
                                'admin.ticket_reassigned', 'admin.ticket_unassigned',
                                'admin.ticket_resolved', 'admin.ticket_reopened', 'admin.ticket_deleted',
                                'security.admin_denied', 'security.reauth_failed')),
    target_type text        CONSTRAINT audit_log_target_type_check
                            CHECK (target_type IS NULL OR target_type IN ('user', 'ticket', 'note')),
    target_id   bigint,
    summary     text        NOT NULL
                            CONSTRAINT audit_log_summary_check CHECK (char_length(summary) BETWEEN 1 AND 500),
    -- Structured details (before / after values, reasons). Never secrets: the check below refuses
    -- the obvious top-level keys; the application builds this object from a fixed list of fields.
    detail      jsonb       CONSTRAINT audit_log_detail_check CHECK (
                                detail IS NULL OR (
                                    jsonb_typeof(detail) = 'object'
                                    AND NOT (detail ?| ARRAY['password', 'password_hash', 'hash', 'token',
                                                             'session', 'secret', 'cookie', 'temporary_password']))),
    ip          inet
);

CREATE INDEX IF NOT EXISTS audit_log_actor_idx        ON audit_log (actor_id, id DESC);
CREATE INDEX IF NOT EXISTS audit_log_action_idx       ON audit_log (action, id DESC);
CREATE INDEX IF NOT EXISTS audit_log_target_idx       ON audit_log (target_type, target_id, id DESC);
CREATE INDEX IF NOT EXISTS audit_log_actor_action_idx ON audit_log (actor_id, action, id DESC);

-- Append-only: refuse UPDATE, DELETE and TRUNCATE for everybody (the table owner could still
-- disable these triggers on purpose; the application can not).
CREATE OR REPLACE FUNCTION audit_log_immutable() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'audit_log is append-only: % is not allowed', TG_OP
        USING ERRCODE = '42501';
END
$$;

DROP TRIGGER IF EXISTS audit_log_no_change   ON audit_log;
CREATE TRIGGER audit_log_no_change
    BEFORE UPDATE OR DELETE ON audit_log
    FOR EACH ROW EXECUTE FUNCTION audit_log_immutable();

DROP TRIGGER IF EXISTS audit_log_no_truncate ON audit_log;
CREATE TRIGGER audit_log_no_truncate
    BEFORE TRUNCATE ON audit_log
    FOR EACH STATEMENT EXECUTE FUNCTION audit_log_immutable();

-- ---------------------------------------------------------------------------------------------
-- 3) Admin protections on users
-- ---------------------------------------------------------------------------------------------

-- 3a) Only the database terminal can create, change or remove an Admin.
--     "Touching an Admin" means: inserting an Admin; deleting an Admin; or changing role, is_active
--     or first_name of a row that is, or becomes, an Admin. A password change is NOT blocked (an
--     Admin must be able to change their own password later).
--     Allowed only when the session says  SET LOCAL app.admin_change = 'allowed';  (the application
--     never sets it) and never for the login role app_user, even with the flag.
CREATE OR REPLACE FUNCTION users_admin_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    touches boolean;
BEGIN
    IF TG_OP = 'INSERT' THEN
        touches := NEW.role = 'Admin';
    ELSIF TG_OP = 'DELETE' THEN
        touches := OLD.role = 'Admin';
    ELSE
        touches := (OLD.role = 'Admin' OR NEW.role = 'Admin')
                   AND (OLD.role IS DISTINCT FROM NEW.role
                        OR OLD.is_active IS DISTINCT FROM NEW.is_active
                        OR OLD.first_name IS DISTINCT FROM NEW.first_name);
    END IF;

    IF touches AND (session_user = 'app_user'
                    OR coalesce(current_setting('app.admin_change', true), '') <> 'allowed') THEN
        RAISE EXCEPTION 'Admin accounts can only be created, changed or removed from the database terminal (see db/ADMIN_PROCEDURES.md)'
            USING ERRCODE = '42501';
    END IF;

    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;
    RETURN NEW;
END
$$;

DROP TRIGGER IF EXISTS users_admin_guard ON users;
CREATE TRIGGER users_admin_guard
    BEFORE INSERT OR UPDATE OR DELETE ON users
    FOR EACH ROW EXECUTE FUNCTION users_admin_guard();

-- 3b) The last active Admin cannot disappear (also not from the terminal).
--     A deferred constraint trigger: the rule is checked at COMMIT, when the whole transaction is
--     visible, so "promote B, then demote A" in one transaction works in either order, and a
--     statement that demotes every Admin at once is refused. An advisory lock serializes two
--     transactions that both remove an Admin at the same time: the second one waits for the first
--     to commit, then counts again and is refused.
CREATE OR REPLACE FUNCTION users_keep_one_admin() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    removing boolean;
BEGIN
    IF TG_OP = 'DELETE' THEN
        removing := OLD.role = 'Admin' AND OLD.is_active;
    ELSE
        removing := OLD.role = 'Admin' AND OLD.is_active
                    AND (NEW.role <> 'Admin' OR NOT NEW.is_active);
    END IF;

    IF removing THEN
        PERFORM pg_advisory_xact_lock(hashtext('it_db.users_keep_one_admin'));
        IF NOT EXISTS (SELECT 1 FROM users WHERE role = 'Admin' AND is_active) THEN
            RAISE EXCEPTION 'Refused: this would leave no active Admin. Create or reactivate another Admin first.'
                USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NULL;
END
$$;

DROP TRIGGER IF EXISTS users_keep_one_admin ON users;
CREATE CONSTRAINT TRIGGER users_keep_one_admin
    AFTER UPDATE OR DELETE ON users
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION users_keep_one_admin();

-- 3c) The database writes the audit rows for user changes, whoever makes them.
--     The application sets, inside its own transaction:
--         SELECT set_config('app.actor_id', '<admin id>', true);      -- and optionally app.actor_ip
--     From the terminal nothing is set and the row says "database terminal".
--     Written here: role changes (including creating an Admin), deactivation, reactivation, rename.
--     NOT written here (the application writes them): registration, admin-created users, passwords.
CREATE OR REPLACE FUNCTION users_audit() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_actor_id   bigint := nullif(current_setting('app.actor_id', true), '')::bigint;
    v_actor_name text;
    v_actor_role text;
    v_ip         inet   := nullif(current_setting('app.actor_ip', true), '')::inet;
    v_via        text;
BEGIN
    IF v_actor_id IS NOT NULL THEN
        SELECT first_name, role INTO v_actor_name, v_actor_role FROM users WHERE id = v_actor_id;
    END IF;
    IF v_actor_name IS NULL THEN
        v_actor_id := NULL;
        v_actor_name := 'database terminal';
        v_actor_role := NULL;
        v_via := 'terminal';
    ELSE
        v_via := 'app';
    END IF;

    IF TG_OP = 'INSERT' THEN
        IF NEW.role = 'Admin' THEN
            INSERT INTO audit_log (actor_id, actor_name, actor_role, action, target_type, target_id, summary, detail, ip)
            VALUES (v_actor_id, v_actor_name, v_actor_role, 'admin.role_changed', 'user', NEW.id,
                    left(format('User %s was created as Admin', NEW.first_name), 500),
                    jsonb_build_object('from', NULL, 'to', NEW.role, 'via', v_via), v_ip);
        END IF;
        RETURN NULL;
    END IF;

    IF NEW.role IS DISTINCT FROM OLD.role THEN
        INSERT INTO audit_log (actor_id, actor_name, actor_role, action, target_type, target_id, summary, detail, ip)
        VALUES (v_actor_id, v_actor_name, v_actor_role, 'admin.role_changed', 'user', NEW.id,
                left(format('Role of %s changed from %s to %s', NEW.first_name, OLD.role, NEW.role), 500),
                jsonb_build_object('from', OLD.role, 'to', NEW.role, 'via', v_via), v_ip);
    END IF;

    IF OLD.is_active AND NOT NEW.is_active THEN
        INSERT INTO audit_log (actor_id, actor_name, actor_role, action, target_type, target_id, summary, detail, ip)
        VALUES (v_actor_id, v_actor_name, v_actor_role, 'admin.user_deactivated', 'user', NEW.id,
                left(format('User %s was deactivated', NEW.first_name), 500),
                jsonb_build_object('role', NEW.role, 'via', v_via), v_ip);
    ELSIF NOT OLD.is_active AND NEW.is_active THEN
        INSERT INTO audit_log (actor_id, actor_name, actor_role, action, target_type, target_id, summary, detail, ip)
        VALUES (v_actor_id, v_actor_name, v_actor_role, 'admin.user_reactivated', 'user', NEW.id,
                left(format('User %s was reactivated', NEW.first_name), 500),
                jsonb_build_object('role', NEW.role, 'via', v_via), v_ip);
    END IF;

    IF NEW.first_name IS DISTINCT FROM OLD.first_name THEN
        INSERT INTO audit_log (actor_id, actor_name, actor_role, action, target_type, target_id, summary, detail, ip)
        VALUES (v_actor_id, v_actor_name, v_actor_role, 'admin.user_updated', 'user', NEW.id,
                left(format('Username changed from %s to %s', OLD.first_name, NEW.first_name), 500),
                jsonb_build_object('from', OLD.first_name, 'to', NEW.first_name, 'via', v_via), v_ip);
    END IF;

    RETURN NULL;
END
$$;

DROP TRIGGER IF EXISTS users_audit ON users;
CREATE TRIGGER users_audit
    AFTER INSERT OR UPDATE ON users
    FOR EACH ROW EXECUTE FUNCTION users_audit();

-- ---------------------------------------------------------------------------------------------
-- 4) Indexes for the admin lists (tickets and notes already exist; these are additions)
-- ---------------------------------------------------------------------------------------------
CREATE INDEX IF NOT EXISTS tickets_assigned_to_idx  ON tickets (assigned_to, status);
CREATE INDEX IF NOT EXISTS tickets_priority_idx     ON tickets (priority, created_at DESC);
CREATE INDEX IF NOT EXISTS ticket_notes_author_idx  ON ticket_notes (author_id, id DESC);
CREATE INDEX IF NOT EXISTS ticket_notes_created_idx ON ticket_notes (created_at DESC, id DESC);

-- ---------------------------------------------------------------------------------------------
-- 5) Grants for the limited app user, only if that role exists (db/app_user.sql is the full set).
--    audit_log: read and add rows, never change or remove them.
--    users: the application may later change these columns and nothing else.
-- ---------------------------------------------------------------------------------------------
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'app_user') THEN
        GRANT SELECT, INSERT ON audit_log TO app_user;
        REVOKE UPDATE, DELETE, TRUNCATE ON audit_log FROM app_user;
        GRANT UPDATE (first_name, role, is_active, password, must_change_password) ON users TO app_user;
        GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO app_user;
    END IF;
END
$$;

COMMIT;
