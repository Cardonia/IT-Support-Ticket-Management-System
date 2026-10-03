-- Read-only permission report. Changes nothing.
-- Run it as the database user the APPLICATION connects with (the user in appsettings.json):
--   psql -h localhost -U <app connection user> -d it_db -f db/check_permissions.sql
-- Then send me the output (it contains no passwords or data, only names and yes/no answers).
--
-- It answers: who is the app connecting as, does that user own the tables or is it a superuser,
-- which migrations have been applied, does the role app_user exist, and what exactly could
-- app_user do right now compared with what the application needs.

\echo
\echo '=== 1. Who am I ==='
SELECT current_user AS connected_as,
       session_user AS session_user,
       current_database() AS database,
       (SELECT rolsuper FROM pg_roles WHERE rolname = current_user) AS is_superuser,
       version() AS server_version;

\echo
\echo '=== 2. Tables: owner, and whether I am the owner ==='
SELECT c.relname AS table_name,
       pg_get_userbyid(c.relowner) AS owner,
       pg_get_userbyid(c.relowner) = current_user AS i_am_owner
FROM pg_class c
WHERE c.relnamespace = 'public'::regnamespace
  AND c.relkind = 'r'
  AND c.relname IN ('users', 'sessions', 'tickets', 'ticket_notes', 'audit_log')
ORDER BY c.relname;

\echo
\echo '=== 3. Which migrations are applied ==='
SELECT 'users.role allows Admin (005)' AS check_name,
       coalesce((SELECT pg_get_constraintdef(oid) LIKE '%Admin%' FROM pg_constraint WHERE conname = 'users_role_check'), false) AS yes
UNION ALL SELECT 'users.created_at / is_active / must_change_password (005)',
       (SELECT count(*) = 3 FROM information_schema.columns
         WHERE table_schema = 'public' AND table_name = 'users'
           AND column_name IN ('created_at', 'is_active', 'must_change_password'))
UNION ALL SELECT 'table audit_log (005)', to_regclass('public.audit_log') IS NOT NULL
UNION ALL SELECT 'table tickets (003)',   to_regclass('public.tickets') IS NOT NULL
UNION ALL SELECT 'table ticket_notes (004)', to_regclass('public.ticket_notes') IS NOT NULL
UNION ALL SELECT 'admin triggers on users (005)',
       (SELECT count(*) = 3 FROM pg_trigger
         WHERE tgrelid = 'public.users'::regclass AND NOT tgisinternal
           AND tgname IN ('users_admin_guard', 'users_keep_one_admin', 'users_audit'));

\echo
\echo '=== 4. Does the role app_user exist ==='
SELECT rolname AS role, rolcanlogin AS can_login, rolsuper AS is_superuser, rolcreatedb AS can_create_db,
       rolcreaterole AS can_create_roles
FROM pg_roles WHERE rolname = 'app_user';

\echo
\echo '=== 5. What app_user may do, versus what the application needs ==='
\echo '    (needed = yes means the application runs a statement of that kind; empty result = no app_user)'
SELECT t.table_name,
       t.priv AS privilege,
       t.needed,
       CASE WHEN to_regrole('app_user') IS NULL THEN NULL
            ELSE has_table_privilege('app_user', 'public.' || t.table_name, t.priv) END AS app_user_has_it,
       CASE WHEN to_regrole('app_user') IS NULL THEN NULL
            WHEN t.needed AND NOT has_table_privilege('app_user', 'public.' || t.table_name, t.priv) THEN 'MISSING'
            WHEN NOT t.needed AND has_table_privilege('app_user', 'public.' || t.table_name, t.priv) THEN 'extra (should not have)'
            ELSE 'ok' END AS verdict
FROM (VALUES
    ('users',        'SELECT', true),  ('users',        'INSERT', true),  ('users',        'UPDATE', false), ('users', 'DELETE', false),
    ('sessions',     'SELECT', true),  ('sessions',     'INSERT', true),  ('sessions',     'DELETE', true),  ('sessions', 'UPDATE', false),
    ('tickets',      'SELECT', true),  ('tickets',      'INSERT', true),  ('tickets',      'UPDATE', true),  ('tickets', 'DELETE', false),
    ('ticket_notes', 'SELECT', true),  ('ticket_notes', 'INSERT', true),  ('ticket_notes', 'UPDATE', false), ('ticket_notes', 'DELETE', false)
) AS t(table_name, priv, needed)
WHERE to_regclass('public.' || t.table_name) IS NOT NULL
ORDER BY t.table_name, t.priv;

\echo
\echo '    audit_log (only after migration 005): app_user must be able to read and add rows, never change or remove them'
SELECT p.priv AS privilege,
       CASE WHEN to_regrole('app_user') IS NULL OR to_regclass('public.audit_log') IS NULL THEN NULL
            ELSE has_table_privilege('app_user', 'public.audit_log', p.priv) END AS app_user_has_it,
       p.wanted AS should_have_it
FROM (VALUES ('SELECT', true), ('INSERT', true), ('UPDATE', false), ('DELETE', false), ('TRUNCATE', false)) AS p(priv, wanted);

\echo
\echo '    users columns app_user may update (column level; only after db/app_user.sql)'
SELECT c.column_name,
       CASE WHEN to_regrole('app_user') IS NULL THEN NULL
            ELSE has_column_privilege('app_user', 'public.users', c.column_name, 'UPDATE') END AS app_user_can_update
FROM information_schema.columns c
WHERE c.table_schema = 'public' AND c.table_name = 'users'
ORDER BY c.ordinal_position;

\echo
\echo '=== 6. Sequences app_user can use (inserts need USAGE) ==='
SELECT c.relname AS sequence,
       CASE WHEN to_regrole('app_user') IS NULL THEN NULL
            ELSE has_sequence_privilege('app_user', c.oid, 'USAGE') END AS app_user_has_usage
FROM pg_class c WHERE c.relnamespace = 'public'::regnamespace AND c.relkind = 'S' ORDER BY c.relname;

\echo
\echo '=== 7. Accounts (counts only, no names) ==='
SELECT role, count(*) AS users FROM users GROUP BY role ORDER BY role;

\echo
\echo '=== End of report ==='
