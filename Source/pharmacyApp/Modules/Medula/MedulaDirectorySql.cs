namespace pharmacyApp.Modules.Medula;

internal static class MedulaDirectorySql
{
    public const string InsertDoctorIfMissing = """
        INSERT INTO public.doctor (
            diploma_registration_no, diploma_no, first_name, last_name, branch_id)
        VALUES (
            @registration_no, NULLIF(@diploma_no, ''), NULLIF(@first_name, ''),
            NULLIF(@last_name, ''), @branch_id)
        ON CONFLICT (diploma_registration_no) DO NOTHING;
        """;
}
