-- Level 34: Admin dashboard
-- Run once as the DB admin / table owner (NOT app_user), after db/006_ticket_admin.sql:
--   psql -h localhost -U <admin> -d it_db -f db/007_dashboard.sql
-- Idempotent: safe to run again. It only adds one index; no data, column, trigger or grant changes.
--
-- Why
--   The dashboard counts "the last 24 hours" of the audit log (logins, failed logins, admin actions, security
--   events) and the activity page filters by a date range. Both read audit_log by time, and the log has no index on
--   `at`: PostgreSQL has to read every row (about 10 ms for 100,000 rows, growing with the log). With this index the
--   work is proportional to the 24 hours only.
--   The log is append-only and `at` grows with `id`, so the index is cheap to maintain (new entries land at one end).
--
-- The application also works without this file (the counters are just slower on a large log), so deploying the
-- code first and running the file afterwards is safe.

CREATE INDEX IF NOT EXISTS audit_log_at_idx ON audit_log (at DESC);
