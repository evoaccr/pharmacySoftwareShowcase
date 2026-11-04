namespace pharmacyApp.Modules.Cards;

public enum CardEntityKind { Users, Doctors, Pharmacies, Faculties, Institutions, Depots, Patients, Branches, FacultyTypes, FamilyGroups }
public enum CardFieldKind
{
    Text,
    MultilineText,
    Integer,
    Decimal,
    Date,
    Boolean,
    TextArray,
    Photo,
    Permission,
    Phone,
    Email,
    PhoneList,
    EmailList,
    FaxList,
    UtsCompanyList,
    Gender,
    Ownership,
    CareLevel,
    PatientType,
    Website,
    Lookup,
    FamilyGroup
}

public enum PhonePurposeProfile { None, Personal, Business, Pharmacy }

public sealed record CardFieldDefinition(
    string ColumnName,
    string Label,
    CardFieldKind Kind = CardFieldKind.Text,
    bool Required = false,
    bool ShowOnAdd = true,
    bool ShowOnEdit = true,
    CardEntityKind? LookupTarget = null,
    string? GroupName = null,
    string? Placeholder = null,
    object? DefaultValue = null,
    string? Section = null,
    bool StoreUppercase = false,
    bool ConfirmOnChange = false,
    PhonePurposeProfile PhonePurposeProfile = PhonePurposeProfile.None,
    string? DefaultPhonePurpose = null);

public sealed record CardColumnDefinition(
    string Key, string Header, double Width, bool IsStar = false, string? StringFormat = null,
    string? SortKey = null);

public sealed record CardListQuery(
    string Search = "",
    string SecondarySearch = "",
    int? MinimumAge = null,
    int? MaximumAge = null,
    string DebtState = "Hepsi",
    string ConsignmentState = "Hepsi",
    string Institution = "",
    string FamilyGroup = "",
    string Specialty = "",
    string Facility = "",
    bool PharmacyDebtOnly = false,
    string SortColumn = "",
    bool SortDescending = false);

public sealed record CardListPage(IReadOnlyList<CardRow> Rows, int TotalCount);

public sealed record FamilyGroupDetail(
    int RecordId,
    string Name,
    CardLookupOption? MainPerson,
    IReadOnlyList<CardLookupOption> Members);

public sealed record CardEntityDefinition(
    CardEntityKind Kind,
    string ModuleId,
    string ButtonText,
    string ListTitle,
    string SingularName,
    string AddTitle,
    string EditTitle,
    string AddButtonText,
    string EditMenuText,
    string DeleteButtonText,
    string TableName,
    string KeyColumn,
    IReadOnlyList<CardFieldDefinition> Fields,
    IReadOnlyList<CardColumnDefinition> Columns,
    IReadOnlyList<string>? DisplayNameColumns = null,
    CardFieldKind KeyKind = CardFieldKind.Integer)
{
    public bool IsUsers => Kind == CardEntityKind.Users;
    public bool IsPharmacies => Kind == CardEntityKind.Pharmacies;
    public bool IsPatients => Kind == CardEntityKind.Patients;
    public bool IsDoctors => Kind == CardEntityKind.Doctors;

    public IReadOnlyList<CardFieldDefinition> FieldsFor(bool isAdd) =>
        Fields.Where(field => isAdd ? field.ShowOnAdd : field.ShowOnEdit).ToList();

    public string GetDisplayName(IReadOnlyDictionary<string, object?> values, string fallbackKey)
    {
        var parts = (DisplayNameColumns ?? [KeyColumn])
            .Select(column => values.TryGetValue(column, out var value) ? Convert.ToString(value)?.Trim() : null)
            .Where(value => !string.IsNullOrWhiteSpace(value));
        var displayName = string.Join(" ", parts);
        return string.IsNullOrWhiteSpace(displayName) ? fallbackKey : displayName;
    }
}

public sealed class CardRow
{
    private readonly IReadOnlyDictionary<string, object?> values;

    public CardRow(string key, IReadOnlyDictionary<string, object?> values)
    {
        Key = key;
        this.values = values;
    }

    public string Key { get; }
    public string DisplayName => Convert.ToString(this[nameof(DisplayName)]) ?? Key;
    public object? this[string name] => values.TryGetValue(name, out var value) ? value : null;
    public object? SortValue(string name) => this[name];
    public object? UserId => this[nameof(UserId)];
    public object? FirstName => this[nameof(FirstName)];
    public object? LastName => this[nameof(LastName)];
    public object? PermissionLevel => this[nameof(PermissionLevel)];
    public object? UserPoints => this[nameof(UserPoints)];
    public object? Active => this[nameof(Active)];
    public object? StatusDisplay => this[nameof(StatusDisplay)];
    public object? Specialty => this[nameof(Specialty)];
    public object? FacilityName => this[nameof(FacilityName)];
    public object? PharmacyName => this[nameof(PharmacyName)];
    public object? PharmacistName => this[nameof(PharmacistName)];
    public object? DebtBalanceDisplay => this[nameof(DebtBalanceDisplay)];
    public object? Code => this[nameof(Code)];
    public object? Name => this[nameof(Name)];
    public bool? Status =>
        values.TryGetValue(nameof(Status), out var value) && value is bool status ? status : null;
}

public static partial class CardDefinitions
{
    public static IReadOnlyList<CardEntityDefinition> All =>
        [Users, Doctors, Pharmacies, Faculties, Institutions, Depots, Patients];
}
