BEGIN;

ALTER TABLE public.addresses
    ADD COLUMN IF NOT EXISTS gln text NOT NULL DEFAULT '';

-- The compact invoice schema previously placed Farmassist GLNs in identity_no.
-- Move only structurally valid 13-digit values; VKN/TCKN and synthetic fallback
-- identifiers are preserved as-is.
UPDATE public.addresses
SET gln = btrim(identity_no),
    identity_no = ''
WHERE btrim(identity_no) ~ '^[0-9]{13}$'
  AND btrim(gln) = '';

CREATE UNIQUE INDEX IF NOT EXISTS addresses_gln_unique_idx
    ON public.addresses ((btrim(gln)))
    WHERE btrim(gln) <> '';

COMMENT ON COLUMN public.addresses.gln IS
    '13-digit Global Location Number; an address may be identified by this value, identity_no, or both.';

COMMIT;
