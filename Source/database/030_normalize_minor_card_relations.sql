BEGIN;

CREATE TABLE IF NOT EXISTS public.branches (
    branch_id integer PRIMARY KEY,
    branch_name varchar NOT NULL,
    parent_branch_id integer REFERENCES public.branches(branch_id),
    display_order integer NOT NULL UNIQUE
);

INSERT INTO public.branches (branch_id, branch_name, parent_branch_id, display_order) VALUES
    (1, 'Pratisyen Hekim', NULL, 1),
    (2, 'Aile Hekimliği Uzmanı', NULL, 2),
    (3, 'Acil Tıp', NULL, 3),
    (4, 'Adli Tıp', NULL, 4),
    (5, 'Ağız, Diş, Çene Hastalıkları ve Cerrahisi', NULL, 5),
    (6, 'Anatomi', NULL, 6),
    (7, 'Anesteziyoloji ve Reanimasyon', NULL, 7),
    (8, 'Algoloji', 7, 8),
    (9, 'Yoğun Bakım', 7, 9),
    (10, 'Askeri Sahra Hekimliği', NULL, 10),
    (11, 'Beyin ve Sinir Cerrahisi', NULL, 11),
    (12, 'Çocuk Cerrahisi', NULL, 12),
    (13, 'Çocuk Ürolojisi', 12, 13),
    (14, 'Çocuk Ürolojisi (2)', 12, 14),
    (15, 'Çocuk ve Ergen Ruh Sağlığı ve Hastalıkları', NULL, 15),
    (16, 'Çocuk Sağlığı ve Hastalıkları', NULL, 16),
    (17, 'Yoğun Bakım', 16, 17),
    (18, 'Neonatoloji', 16, 18),
    (19, 'Genetik', 16, 19),
    (20, 'Çocuk Onkolojisi', 16, 20),
    (21, 'Çocuk Nörolojisi', 16, 21),
    (22, 'Çocuk Nefrolojisi', 16, 22),
    (23, 'Çocuk Metabolizma Hastalıkları', 16, 23),
    (24, 'Çocuk Kardiyolojisi', 16, 24),
    (25, 'Çocuk İmmünolojisi', 16, 25),
    (26, 'Çocuk Hematolojisi', 16, 26),
    (27, 'Çocuk Hematolojisi ve Onkolojisi', 16, 27),
    (28, 'Çocuk Göğüs Hastalıkları', 16, 28),
    (29, 'Çocuk Gastroenteroloji, Hepatoloji ve Beslenme', 16, 29),
    (30, 'Çocuk Enfeksiyon Hastalıkları', 16, 30),
    (31, 'Çocuk Endokrinolojisi', 16, 31),
    (32, 'Çocuk İmmünolojisi ve Alerji Hastalıkları', 16, 32),
    (33, 'Çocuk Romatolojisi', 16, 33),
    (34, 'Çocuk Endokrinolojik ve Metabolizma Hastalıkları', 16, 34),
    (35, 'Çocuk Acil', 16, 35),
    (36, '__Çocuk İmmünolojisi ve Alerji Hastalıkları', 16, 36),
    (37, 'Gelişimsel Pediatri', 16, 37),
    (38, 'Çocuk Alerjisi', 16, 38),
    (39, 'Deri ve Zührevi Hastalıkları', NULL, 39),
    (40, 'İmmünoloji ve Alerji Hastalıkları', 39, 40),
    (41, 'Diş Hastalıkları ve Tedavisi', NULL, 41),
    (42, 'Diş Protezi', NULL, 42),
    (43, 'El Cerrahisi(Ortopedi ve Travmatoloji)', NULL, 43),
    (44, 'Embriyoloji ve Histoloji', NULL, 44),
    (45, 'Enfeksiyon Hastalıkları ve Klinik Mikrobiyoloji', NULL, 45),
    (46, 'Yoğun Bakım', 45, 46),
    (47, 'Endodonti', NULL, 47),
    (48, 'Fiziksel Tıp ve Rehabilitasyon', NULL, 48),
    (49, 'Romatoloji (Fizik Tedavi)', 48, 49),
    (50, 'Algoloji (Fizik Tedavi)', 48, 50),
    (51, 'Fizyoloji', NULL, 51),
    (52, 'Genel Cerrahi', NULL, 52),
    (53, 'Gastroentroloji Cerrahisi', 52, 53),
    (54, 'Cerrahi Onkoloji', 52, 54),
    (55, 'El Cerrahisi', 52, 55),
    (56, 'Yoğun Bakım', 52, 56),
    (57, 'Göğüs Cerrahisi', NULL, 57),
    (58, 'Göğüs Hastalıkları', NULL, 58),
    (59, 'İmmünoloji ve Alerji Hastalıkları', 58, 59),
    (60, 'Yoğun Bakım', 58, 60),
    (61, '__İmmünoloji ve Alerji Hastalıkları', 58, 61),
    (62, 'Tüberküloz, Allerjik Hastalıklar', 58, 62),
    (63, 'Göz Hastalıkları', NULL, 63),
    (64, 'Halk Sağlığı', NULL, 64),
    (65, 'Okul Hekimliği', 64, 65),
    (66, 'İşyeri Hekimliği', 64, 66),
    (67, 'Çevre Sağlığı', 64, 67),
    (68, 'Hava ve Uzay Hekimliği', NULL, 68),
    (69, 'İç Hastalıkları', NULL, 69),
    (70, 'Yoğun Bakım', 69, 70),
    (71, 'Tıbbi Onkoloji', 69, 71),
    (72, 'Romatoloji', 69, 72),
    (73, 'İş ve Meslek Hastalıkları', 69, 73),
    (74, 'İmmünoloji ve Alerji Hastalıklar', 69, 74),
    (75, 'Hematoloji', 69, 75),
    (76, 'Geriatri', 69, 76),
    (77, 'Gastroenteroloji', 69, 77),
    (78, 'Alerji Hastalıkları', 69, 78),
    (79, 'Nefroloji', 69, 79),
    (80, 'Endokrinoloji ve Metabolizma Hastalıkları', 69, 80),
    (81, 'İnfeksiyon Hastalıkları', 69, 81),
    (82, '__İmmünoloji ve Alerji Hastalıklar', 69, 82),
    (83, 'Kadın Hastalıkları ve Doğum', NULL, 83),
    (84, 'Üreme Endokrinolojisi ve İnfertilite', 83, 84),
    (85, 'Perinatoloji(Kadın Doğum)', 83, 85),
    (86, 'Jinekolojik Onkoloji Cerrahisi (Kadın Hast.ve Doğum)', 83, 86),
    (87, 'Kalp ve Damar Cerrahisi', NULL, 87),
    (88, 'Çocuk Kalp ve Damar Cerrahisi', 87, 88),
    (89, 'Kardiyoloji', NULL, 89),
    (90, 'Kulak-Burun-Boğaz Hastalıkları', NULL, 90),
    (91, 'Nöroloji', NULL, 91),
    (92, 'Klinik Nörofizyoloji', 91, 92),
    (93, 'Algoloji (Nöroloji)', 91, 93),
    (94, 'Yoğun Bakım (Nöroloji)', 91, 94),
    (95, 'Nükleer Tıp', NULL, 95),
    (96, 'Oral Diagnoz ve Radyoloji', NULL, 96),
    (97, 'Ortodonti', NULL, 97),
    (98, 'Ortopedi ve Travmatoloji', NULL, 98),
    (99, 'Pedodonti', NULL, 99),
    (100, 'Periodontoloji', NULL, 100),
    (101, 'Plastik, Rekonstrüktif ve Estetik Cerrahi', NULL, 101),
    (102, 'El Cerrahisi', 101, 102),
    (103, 'Ağız, Yüz ve Çene Cerrahisi', 101, 103),
    (104, 'Ruh Sağlığı ve Hastalıkları', NULL, 104),
    (105, 'Radyasyon Onkolojisi', NULL, 105),
    (106, 'Radyoloji', NULL, 106),
    (107, 'Pediyatrik Radyoloji', 106, 107),
    (108, 'Nöroradyoloji', 106, 108),
    (109, 'Girişimsel Radyoloji', 106, 109),
    (110, 'Restoratif Diş Tedavisi', NULL, 110),
    (111, 'Spor Hekimliği', NULL, 111),
    (112, 'Sualtı Hekimliği ve Hiperbarik Tıp', NULL, 112),
    (113, 'Tıbbi Biyokimya', NULL, 113),
    (114, 'Tıbbi Ekoloji ve Hidroklimatoloji', NULL, 114),
    (115, 'Tıbbi Farmakoloji', NULL, 115),
    (116, 'Toksikoloji', 115, 116),
    (117, 'Tıbbi Genetik', NULL, 117),
    (118, 'Klinik Sitogenetik', 117, 118),
    (119, 'Klinik Moleküler Genetik', 117, 119),
    (120, 'Klinik Genetik', 117, 120),
    (121, 'Tıbbi Mikrobiyoloji', NULL, 121),
    (122, 'Viroloji', 121, 122),
    (123, 'Tıbbi Parazitoloji', 121, 123),
    (124, 'Mikoloji', 121, 124),
    (125, 'İmmünoloji', 121, 125),
    (126, 'Tıbbi Patoloji', NULL, 126),
    (127, 'Sitopatoloji', 126, 127),
    (128, 'Nöropatoloji', 126, 128),
    (129, 'Dermatopatoloji', 126, 129),
    (130, 'Toksikoloji(Adli Tıp)', NULL, 130),
    (131, 'Üroloji', NULL, 131),
    (132, 'Çocuk Ürolojisi', 131, 132),
    (133, 'Androloji', 131, 133),
    (134, 'Hemodiyaliz Kliniği', NULL, 134),
    (135, 'İlk ve Acil Yardım', NULL, 135),
    (136, '__Deri ve Zührevi Hastalıkları', NULL, 136),
    (137, 'Fizikoterapi ve İdroterapi', NULL, 137),
    (138, 'Diğer Branş', NULL, 138)
ON CONFLICT (branch_id) DO UPDATE SET
    branch_name = EXCLUDED.branch_name,
    parent_branch_id = EXCLUDED.parent_branch_id,
    display_order = EXCLUDED.display_order;

CREATE INDEX IF NOT EXISTS ix_branches_parent_branch_id
    ON public.branches(parent_branch_id);

ALTER TABLE public.doctor
    ADD COLUMN IF NOT EXISTS branch_id integer;

DO $$
DECLARE
    unmapped_count integer;
    unmapped_value record;
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema='public' AND table_name='doctor' AND column_name='specialty'
    ) THEN
        WITH source_values AS (
            SELECT d.record_id,
                   BTRIM(REPLACE(REPLACE(d.specialty[1], '-&gt;', '->'), ' -&gt; ', ' -> ')) AS raw_value
            FROM public.doctor d
            WHERE cardinality(d.specialty) > 0
        ), normalized AS (
            SELECT record_id,
                   BTRIM(REGEXP_REPLACE(raw_value, '\s*\(Ana Branş\)\s*$', '', 'i')) AS value,
                   BTRIM(SPLIT_PART(raw_value, '->', 1)) AS parent_value,
                   BTRIM(REGEXP_REPLACE(SPLIT_PART(raw_value, '->', 2), '\s*\(Ana Branş\)\s*$', '', 'i')) AS child_value
            FROM source_values
        ), resolved AS (
            SELECT n.record_id,
                   COALESCE(
                       (SELECT child.branch_id
                        FROM public.branches child
                        JOIN public.branches parent ON parent.branch_id=child.parent_branch_id
                        WHERE n.value LIKE '%->%'
                          AND LOWER(parent.branch_name)=LOWER(n.parent_value)
                          AND LOWER(REPLACE(child.branch_name, ' ', ''))=LOWER(REPLACE(n.child_value, ' ', ''))
                        ORDER BY child.display_order LIMIT 1),
                       (SELECT b.branch_id FROM public.branches b
                        WHERE b.parent_branch_id IS NULL AND LOWER(b.branch_name)=LOWER(n.value)
                        ORDER BY b.display_order LIMIT 1),
                       CASE
                           WHEN LOWER(n.value)='dermatoloji' THEN 39
                           WHEN LOWER(n.value)='psikiyatri' THEN 104
                           WHEN LOWER(n.value)='enfeksiyon hastalıkları' THEN 45
                           WHEN LOWER(n.value)='çocuk psikiyatrisi' THEN 15
                           WHEN LOWER(n.value)='göğüs hastalıkları -> alerjik göğüs hastalıkları' THEN 62
                           WHEN LOWER(n.value)='iç hastalıkları -> immünoloji' THEN 74
                           WHEN n.value ILIKE 'Çocuk Sağlığı ve Hastalıkları%Çocuk Romatoloji' THEN 33
                           WHEN n.value ILIKE 'İç Hastalıkları%İmmünoloji ve Alerji Hastalıkları' THEN 74
                           WHEN n.value ILIKE 'İç Hastalıkları%İmmünoloji' THEN 74
                           ELSE NULL
                       END
                   ) AS branch_id
            FROM normalized n
        )
        UPDATE public.doctor d
        SET branch_id=r.branch_id
        FROM resolved r
        WHERE r.record_id=d.record_id;

        SELECT COUNT(*) INTO unmapped_count
        FROM public.doctor
        WHERE cardinality(specialty)>0 AND branch_id IS NULL;
        IF unmapped_count > 0 THEN
            FOR unmapped_value IN
                SELECT specialty[1] AS value, COUNT(*) AS row_count
                FROM public.doctor
                WHERE cardinality(specialty)>0 AND branch_id IS NULL
                GROUP BY specialty[1]
                ORDER BY specialty[1]
            LOOP
                RAISE NOTICE 'Unmapped specialty: % (% row(s))', unmapped_value.value, unmapped_value.row_count;
            END LOOP;
            RAISE EXCEPTION '% doctor specialty value(s) could not be mapped to branches', unmapped_count;
        END IF;

        ALTER TABLE public.doctor DROP COLUMN specialty;
    END IF;
END $$;

ALTER TABLE public.doctor
    DROP COLUMN IF EXISTS phone_number,
    DROP COLUMN IF EXISTS email_address;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid='public.doctor'::regclass AND conname='doctor_branch_id_fkey'
    ) THEN
        ALTER TABLE public.doctor
            ADD CONSTRAINT doctor_branch_id_fkey
            FOREIGN KEY (branch_id) REFERENCES public.branches(branch_id);
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS public.faculty_type (
    faculty_type_id integer PRIMARY KEY,
    faculty_type_name varchar NOT NULL
);

INSERT INTO public.faculty_type (faculty_type_id, faculty_type_name) VALUES
    (1, 'AİLE HEKİMLİĞİ'),
    (2, 'SAĞLIK BAKANLIĞI - 2. BASAMAK SAĞLIK TESİSLERİ'),
    (3, 'SAĞLIK BAKANLIĞI - 1. BASAMAK SAĞLIK TESİSLERİ'),
    (4, 'SAĞLIK BAKANLIĞI - 3. BASAMAK SAĞLIK TESİSLERİ'),
    (5, 'TSK - 1. BASAMAK SAĞLIK TESİSLERİ'),
    (6, 'TSK - 2. BASAMAK SAĞLIK TESİSLERİ'),
    (7, 'TSK - 3. BASAMAK SAĞLIK TESİSLERİ'),
    (8, 'TSK - 3. BASAMAK SAĞLIK TESİSLERİ'),
    (9, 'ÜNİVERSİTE - 1. BASAMAK SAĞLIK TESİSLERİ'),
    (10, 'ÜNİVERSİTE - 2. BASAMAK SAĞLIK TESİSLERİ'),
    (11, 'ÜNİVERSİTE - 3. BASAMAK SAĞLIK TESİSLERİ'),
    (12, 'VAKIF ÜNİVERSİTESİ SAĞLIK TESİSLERİ'),
    (13, 'KİT, BELEDİYE VE BAKANLIK  - 1. BAS. SAĞLIK TES.'),
    (14, 'KİT, BELEDİYE VE BAKANLIK  - 1. BAS. SAĞLIK TES.'),
    (15, 'KİT, BELEDİYE VE BAKANLIK  - 2. BAS. SAĞLIK TES.'),
    (16, 'KİT, BELEDİYE VE BAKANLIK  - 3. BAS. SAĞLIK TES.'),
    (17, 'ÖZEL - 2. BASAMAK SAĞLIK TESİSLERİ'),
    (18, 'ÖZEL - 3. BASAMAK SAĞLIK TESİSLERİ'),
    (19, 'İŞ YERİ HEKİMLERİ'),
    (20, 'KURUM - 1. BASAMAK KURUM HEKİMLİKLERİ'),
    (21, 'ÖZEL-VAKIF Y.Ö.K''LARI İLE PROTOKOLU BULUNAN SHS'),
    (22, 'MAHKEME KARARI İLE 3. BASAMAK OLARAK TANIMLANAN SHS')
ON CONFLICT (faculty_type_id) DO UPDATE SET
    faculty_type_name=EXCLUDED.faculty_type_name;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema='public' AND table_name='faculty' AND column_name='care_level'
    ) AND NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema='public' AND table_name='faculty' AND column_name='faculty_type_id'
    ) THEN
        ALTER TABLE public.faculty RENAME COLUMN care_level TO faculty_type_id;
    END IF;
END $$;

UPDATE public.faculty
SET faculty_type_id=NULL
WHERE faculty_type_id IS NOT NULL
  AND NOT EXISTS (
      SELECT 1 FROM public.faculty_type ft
      WHERE ft.faculty_type_id=faculty.faculty_type_id
  );

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid='public.faculty'::regclass AND conname='faculty_faculty_type_id_fkey'
    ) THEN
        ALTER TABLE public.faculty
            ADD CONSTRAINT faculty_faculty_type_id_fkey
            FOREIGN KEY (faculty_type_id) REFERENCES public.faculty_type(faculty_type_id);
    END IF;
END $$;

ALTER TABLE public.patient
    DROP CONSTRAINT IF EXISTS patient_kurum_id_fkey;

DO $$
DECLARE
    unmapped_count integer;
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema='public' AND table_name='patient' AND column_name='kurum_id'
          AND data_type='character varying'
    ) THEN
        ALTER TABLE public.patient ADD COLUMN kurum_record_id integer;
        UPDATE public.patient p
        SET kurum_record_id=k.record_id
        FROM public.kurum k
        WHERE k.kurum_code=p.kurum_id;

        SELECT COUNT(*) INTO unmapped_count
        FROM public.patient
        WHERE kurum_id IS NOT NULL AND kurum_record_id IS NULL;
        IF unmapped_count > 0 THEN
            RAISE EXCEPTION '% patient institution value(s) could not be mapped to kurum.record_id', unmapped_count;
        END IF;

        ALTER TABLE public.patient DROP COLUMN kurum_id;
        ALTER TABLE public.patient RENAME COLUMN kurum_record_id TO kurum_id;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid='public.patient'::regclass AND conname='patient_kurum_id_fkey'
    ) THEN
        ALTER TABLE public.patient
            ADD CONSTRAINT patient_kurum_id_fkey
            FOREIGN KEY (kurum_id) REFERENCES public.kurum(record_id);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_patient_kurum_id
    ON public.patient(kurum_id);

ALTER TABLE public.patient
    DROP COLUMN IF EXISTS medication_history,
    DROP COLUMN IF EXISTS examination_history;

ALTER TABLE public.pharmacy
    DROP COLUMN IF EXISTS fax_numbers;

ALTER TABLE public.depot
    ADD COLUMN IF NOT EXISTS debt_balance numeric,
    ADD COLUMN IF NOT EXISTS medicines_in_transit varchar[];

COMMIT;
