-- Level 19: ticket notes
-- Run once as the DB admin (NOT app_user):
--   psql -h localhost -U <admin> -d it_db -f db/004_notes.sql
-- Idempotent: safe to run again. Needs db/003_tickets.sql first (tickets and users tables).
--
-- A note is a short text a technician writes on a ticket they took (1 to 2000 characters, not only
-- spaces / tabs / line breaks; the app trims it first, this constraint is the safety net).
-- Notes are only added (Level 19) and read (Level 20): never edited, never deleted.

BEGIN;

CREATE TABLE IF NOT EXISTS ticket_notes (
    id         bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    ticket_id  bigint      NOT NULL REFERENCES tickets(id),
    author_id  bigint      NOT NULL REFERENCES users(id),
    body       text        NOT NULL
                           CONSTRAINT ticket_notes_body_check
                           CHECK (btrim(body, E' \t\r\n') <> '' AND char_length(body) <= 2000),
    created_at timestamptz NOT NULL DEFAULT now()
);

-- "All notes of one ticket, oldest first" (Level 20)
CREATE INDEX IF NOT EXISTS ticket_notes_ticket_idx ON ticket_notes (ticket_id, created_at, id);

-- Grants for the limited app user. Skipped if that role does not exist (e.g. you still run as admin).
-- No UPDATE and no DELETE: the app never changes or removes a note.
-- The sequences grant is repeated on purpose: ALL SEQUENCES only covers sequences that already
-- exist, and this table just created ticket_notes_id_seq.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'app_user') THEN
        GRANT SELECT, INSERT ON ticket_notes TO app_user;
        GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO app_user;
    END IF;
END
$$;

COMMIT;
