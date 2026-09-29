-- 1) Case-insensitive unique usernames (Sara == sara)
--    Fails if you already have two users that differ only by case; fix those first.
CREATE UNIQUE INDEX IF NOT EXISTS users_first_name_lower_uq
    ON users (lower(first_name));

-- 2) Separate sessions table: many sessions per user, expiry, revocation
CREATE TABLE IF NOT EXISTS sessions (
    token_hash text        PRIMARY KEY,
    user_id    bigint      NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS sessions_user_id_idx ON sessions (user_id);
CREATE INDEX IF NOT EXISTS sessions_expires_at_idx ON sessions (expires_at);

-- 3) The old single-session column is no longer used (everyone must log in again)
ALTER TABLE users DROP COLUMN IF EXISTS session;

-- 4) Don't run the app as "admin": use a limited user
-- CREATE USER app_user WITH PASSWORD '...';
-- GRANT SELECT, INSERT ON users TO app_user;
-- GRANT SELECT, INSERT, DELETE ON sessions TO app_user;
-- GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO app_user;
