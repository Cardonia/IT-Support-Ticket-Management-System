-- Level 8: two real roles (Employee, Technician)
-- Run once as the DB admin (NOT app_user):
--   psql -h localhost -U <admin> -d it_db -f db/002_roles.sql
-- Idempotent: safe to run again.
--
-- Deploy order: run this migration, then deploy the new code.
-- The old code inserts role 'guest', which the CHECK below rejects.

BEGIN;

-- 1) Everyone who was a 'guest' (or has no role) becomes an Employee
UPDATE users SET role = 'Employee' WHERE role IS NULL OR lower(role) = 'guest';

-- 2) New rows default to Employee, and role can no longer be empty
ALTER TABLE users ALTER COLUMN role SET DEFAULT 'Employee';
ALTER TABLE users ALTER COLUMN role SET NOT NULL;

-- 3) Only the two known roles are allowed.
--    If this fails, some row has another role value: look with
--      SELECT id, first_name, role FROM users WHERE role NOT IN ('Employee','Technician');
--    fix those rows, then run this file again (the transaction rolled back, nothing changed).
ALTER TABLE users DROP CONSTRAINT IF EXISTS users_role_check;
ALTER TABLE users ADD CONSTRAINT users_role_check
    CHECK (role IN ('Employee', 'Technician'));

COMMIT;

-- 4) Creating the first technician is manual, on purpose (app_user cannot UPDATE users,
--    so nobody can promote themselves through the app):
--    register the account normally in the UI, then as the DB admin run:
--
--    UPDATE users SET role = 'Technician' WHERE lower(first_name) = lower('the_username');
--
--    That user's next request already sees the new role (it is read from the DB on every request).
