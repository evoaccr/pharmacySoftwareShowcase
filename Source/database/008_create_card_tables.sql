BEGIN;

CREATE TABLE IF NOT EXISTS public.faculty (
    faculty_id integer PRIMARY KEY,
    faculty_name varchar
);

CREATE TABLE IF NOT EXISTS public.kurum (
    kurum_id integer PRIMARY KEY,
    kurum_name varchar
);

CREATE TABLE IF NOT EXISTS public.depot (
    gln_code varchar PRIMARY KEY,
    depot_name varchar
);

CREATE TABLE IF NOT EXISTS public.pharmacy (
    gln_code varchar PRIMARY KEY,
    pharmacy_name varchar,
    pharmacist_name varchar,
    medicines_in_transit varchar[],
    debt_balance numeric
);

CREATE TABLE IF NOT EXISTS public.doctor (
    diploma_registration_no varchar PRIMARY KEY,
    diploma_no varchar,
    first_name varchar,
    last_name varchar,
    specialty varchar,
    certificate varchar,
    facility_no varchar
);

CREATE TABLE IF NOT EXISTS public.users (
    user_id varchar PRIMARY KEY,
    tc_identity_no varchar,
    first_name varchar,
    last_name varchar,
    phone_number varchar,
    gender varchar,
    age integer,
    date_of_birth date,
    photo bytea,
    permisson_level varchar,
    entry_date date,
    exit_date date,
    user_points integer,
    status boolean
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_users_tc_identity_no
    ON public.users (tc_identity_no);

COMMIT;
