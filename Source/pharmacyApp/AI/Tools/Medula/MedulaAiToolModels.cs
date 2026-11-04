using System.Text.Json.Serialization;

namespace pharmacyApp.AI.Tools.Medula;

public sealed record MedulaAiSessionResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("browser_recreated")] bool BrowserRecreated,
    [property: JsonPropertyName("state_restored")] bool StateRestored);

public sealed record MedulaAiPrescriptionListItem(
    [property: JsonPropertyName("prescription_number")] string PrescriptionNumber,
    [property: JsonPropertyName("electronic_prescription_number")] string ElectronicPrescriptionNumber,
    [property: JsonPropertyName("prescription_uid")] string PrescriptionUid,
    [property: JsonPropertyName("version_number")] int VersionNumber,
    [property: JsonPropertyName("patient_tc")] string PatientTcIdentityNo,
    [property: JsonPropertyName("patient_name")] string PatientName,
    [property: JsonPropertyName("prescription_date")] string PrescriptionDate,
    [property: JsonPropertyName("last_transaction_date")] string LastTransactionDate,
    [property: JsonPropertyName("medula_filter_date")] string MedulaFilterDate,
    [property: JsonPropertyName("coverage")] string Coverage,
    [property: JsonPropertyName("qr_status")] string QrStatus,
    [property: JsonPropertyName("prescription_type")] string PrescriptionType,
    [property: JsonPropertyName("invoice_type_code")] string InvoiceTypeCode,
    [property: JsonPropertyName("is_live_on_medula")] bool IsLiveOnMedula,
    [property: JsonPropertyName("is_stored_locally")] bool IsStoredLocally,
    [property: JsonPropertyName("is_deleted")] bool IsDeleted,
    [property: JsonPropertyName("is_permanently_deleted")] bool IsPermanentlyDeleted,
    [property: JsonPropertyName("last_user")] string LastUser)
{
    [JsonPropertyName("total_amount")]
    public string TotalAmount { get; init; } = string.Empty;

    [JsonPropertyName("medical_report")]
    public string MedicalReport { get; init; } = string.Empty;

    [JsonPropertyName("has_medical_report")]
    public bool HasMedicalReport { get; init; }
}

public sealed record MedulaAiPagedResult<T>(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("items")] IReadOnlyList<T> Items,
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("total_count")] int TotalCount,
    [property: JsonPropertyName("next_offset")] int? NextOffset,
    [property: JsonPropertyName("warning")] string? Warning);

public sealed record MedulaAiDrug(
    [property: JsonPropertyName("barcode")] string Barcode,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("quantity")] string Quantity,
    [property: JsonPropertyName("original_quantity")] string? OriginalQuantity,
    [property: JsonPropertyName("period")] string Period,
    [property: JsonPropertyName("period_unit")] string PeriodUnit,
    [property: JsonPropertyName("dose_count")] string DoseCount,
    [property: JsonPropertyName("dose_amount")] string DoseAmount,
    [property: JsonPropertyName("report")] string Report,
    [property: JsonPropertyName("available_date")] string AvailableDate,
    [property: JsonPropertyName("message")] string Message);

public sealed record MedulaAiDiagnosis(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("description")] string Description);

public sealed record MedulaAiLabelValue(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("value")] string Value);

public sealed record MedulaAiPrescriptionDetail(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("warning")] string? Warning,
    [property: JsonPropertyName("prescription_number")] string PrescriptionNumber,
    [property: JsonPropertyName("electronic_prescription_number")] string ElectronicPrescriptionNumber,
    [property: JsonPropertyName("prescription_uid")] string PrescriptionUid,
    [property: JsonPropertyName("version_number")] int VersionNumber,
    [property: JsonPropertyName("patient_tc")] string PatientTcIdentityNo,
    [property: JsonPropertyName("patient_name")] string PatientName,
    [property: JsonPropertyName("is_saved_on_medula")] bool IsSavedOnMedula,
    [property: JsonPropertyName("is_local_archive")] bool IsLocalArchive,
    [property: JsonPropertyName("is_live_on_medula")] bool IsLiveOnMedula,
    [property: JsonPropertyName("fields")] IReadOnlyDictionary<string, string> Fields,
    [property: JsonPropertyName("drugs")] IReadOnlyList<MedulaAiDrug> Drugs,
    [property: JsonPropertyName("diagnoses")] IReadOnlyList<MedulaAiDiagnosis> Diagnoses,
    [property: JsonPropertyName("payments")] IReadOnlyList<MedulaAiLabelValue> Payments);

public sealed record MedulaAiHistoryItem(
    [property: JsonPropertyName("prescription_uid")] string PrescriptionUid,
    [property: JsonPropertyName("version_number")] int VersionNumber,
    [property: JsonPropertyName("action_type")] string ActionType,
    [property: JsonPropertyName("update_datetime")] DateTime UpdateDateTime,
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("changes")] string Changes,
    [property: JsonPropertyName("is_current")] bool IsCurrent);

public sealed record MedulaAiElectronicPrescriptionItem(
    [property: JsonPropertyName("tracking_number")] string TrackingNumber,
    [property: JsonPropertyName("electronic_prescription_number")] string ElectronicPrescriptionNumber,
    [property: JsonPropertyName("prescription_date")] string PrescriptionDate,
    [property: JsonPropertyName("doctor_name")] string DoctorName,
    [property: JsonPropertyName("prescription_type")] string PrescriptionType,
    [property: JsonPropertyName("facility_name")] string FacilityName);

public sealed record MedulaAiElectronicPrescriptionDetail(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("warning")] string? Warning,
    [property: JsonPropertyName("patient_tc")] string PatientTcIdentityNo,
    [property: JsonPropertyName("electronic_prescription_number")] string ElectronicPrescriptionNumber,
    [property: JsonPropertyName("patient_name")] string PatientName,
    [property: JsonPropertyName("patient_fields")] IReadOnlyList<MedulaAiLabelValue> PatientFields,
    [property: JsonPropertyName("prescription_fields")] IReadOnlyList<MedulaAiLabelValue> PrescriptionFields,
    [property: JsonPropertyName("doctor_fields")] IReadOnlyList<MedulaAiLabelValue> DoctorFields,
    [property: JsonPropertyName("drugs")] IReadOnlyList<MedulaAiDrug> Drugs,
    [property: JsonPropertyName("diagnoses")] IReadOnlyList<MedulaAiDiagnosis> Diagnoses,
    [property: JsonPropertyName("signature_text")] string SignatureText,
    [property: JsonPropertyName("can_continue")] bool CanContinue);

public sealed record MedulaAiFilterOption(
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("name")] string Name);

public sealed record MedulaAiFilterOptionsResult(
    [property: JsonPropertyName("invoice_types")] IReadOnlyList<MedulaAiFilterOption> InvoiceTypes,
    [property: JsonPropertyName("periods")] IReadOnlyList<MedulaAiFilterOption> Periods);

public sealed record MedulaAiRemoteField(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("kind")] string Kind);

public sealed record MedulaAiRemoteTable(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("headers")] IReadOnlyList<string> Headers,
    [property: JsonPropertyName("rows")] IReadOnlyList<IReadOnlyList<string>> Rows,
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("total_count")] int TotalCount,
    [property: JsonPropertyName("next_offset")] int? NextOffset);

public sealed record MedulaAiEditableValue(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("value")] string Value);

public sealed record MedulaAiEditableRow(
    [property: JsonPropertyName("medication_name")] string MedicationName,
    [property: JsonPropertyName("values")] IReadOnlyList<MedulaAiEditableValue> Values);

public sealed record MedulaAiRemoteScreenResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("warning")] string? Warning,
    [property: JsonPropertyName("fields")] IReadOnlyList<MedulaAiRemoteField> Fields,
    [property: JsonPropertyName("tables")] IReadOnlyList<MedulaAiRemoteTable> Tables,
    [property: JsonPropertyName("editable_rows")] IReadOnlyList<MedulaAiEditableRow> EditableRows);

public sealed record MedulaAiDrugSearchItem(
    [property: JsonPropertyName("barcode")] string Barcode,
    [property: JsonPropertyName("name")] string Name);

public sealed record MedulaAiDrugSearchResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("warning")] string? Warning,
    [property: JsonPropertyName("items")] IReadOnlyList<MedulaAiDrugSearchItem> Items,
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("total_count")] int TotalCount,
    [property: JsonPropertyName("next_offset")] int? NextOffset);

public sealed record MedulaAiDiagnosisSearchItem(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("description")] string Description);

public sealed record MedulaAiDiagnosisSearchResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("warning")] string? Warning,
    [property: JsonPropertyName("items")] IReadOnlyList<MedulaAiDiagnosisSearchItem> Items,
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("total_count")] int TotalCount,
    [property: JsonPropertyName("next_offset")] int? NextOffset);

public sealed record MedulaAiDoctorSearchResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("warning")] string? Warning,
    [property: JsonPropertyName("fields")] IReadOnlyDictionary<string, string> Fields,
    [property: JsonPropertyName("branch_selection")] MedulaAiRemoteScreenResult? BranchSelection);

public sealed record MedulaAiPrescriptionMessagesResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("prescription_number")] string PrescriptionNumber,
    [property: JsonPropertyName("errors")] IReadOnlyList<string> Errors,
    [property: JsonPropertyName("notices")] IReadOnlyList<string> Notices,
    [property: JsonPropertyName("confirmations")] IReadOnlyList<string> Confirmations,
    [property: JsonPropertyName("drug_errors")] IReadOnlyList<MedulaAiDrugError> DrugErrors);

public sealed record MedulaAiDrugError(
    [property: JsonPropertyName("barcode")] string Barcode,
    [property: JsonPropertyName("error")] string Error);
