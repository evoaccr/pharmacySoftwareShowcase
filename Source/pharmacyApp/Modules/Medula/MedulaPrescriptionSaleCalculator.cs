using pharmacyApp.Modules.Sales;

namespace pharmacyApp.Modules.Medula;

public sealed record MedulaPrescriptionSaleAmounts(
    decimal PatientAmount,
    decimal PrimaryInstitutionAmount,
    decimal SecondaryInstitutionAmount,
    decimal OwedAmount)
{
    public decimal InstitutionAmount => PrimaryInstitutionAmount + SecondaryInstitutionAmount;
    public decimal TotalAmount => PatientAmount + InstitutionAmount;
}

public static class MedulaPrescriptionSaleCalculator
{
    public static MedulaPrescriptionSaleAmounts Calculate(
        string insuranceType,
        decimal? patientTotalPayment,
        decimal? institutionTotalPayment,
        decimal? prescriptionCopaymentCash,
        decimal? examinationCopaymentCash,
        decimal? medicationCopayment)
    {
        var patientAmount = Money(patientTotalPayment);
        var institutionBase = Money(institutionTotalPayment);
        var owedAmount = Money(prescriptionCopaymentCash) + Money(examinationCopaymentCash);
        if (insuranceType.Contains("Emekli", StringComparison.CurrentCultureIgnoreCase))
            return new MedulaPrescriptionSaleAmounts(
                patientAmount,
                institutionBase,
                Money(medicationCopayment),
                owedAmount);
        if (insuranceType.Contains("Çalışan", StringComparison.CurrentCultureIgnoreCase)
            || insuranceType.Contains("Calisan", StringComparison.OrdinalIgnoreCase))
            return new MedulaPrescriptionSaleAmounts(
                patientAmount,
                Math.Max(0m, institutionBase - owedAmount),
                0m,
                owedAmount);
        throw new InvalidOperationException(
            "Hasta sigortalılık türü Çalışan veya Emekli olarak doğrulanamadı; kurum ödemesi sınıflandırılmadı.");
    }

    private static decimal Money(decimal? value) =>
        decimal.Round(Math.Max(0m, value ?? 0m), 2, MidpointRounding.AwayFromZero);
}

public static class MedulaPrescriptionSaleLifecycleRules
{
    public static string NormalizeItsCancellationQr(string? code) =>
        RetailQrCodeNormalizer.Normalize(code ?? string.Empty)
            .Replace("-", string.Empty, StringComparison.Ordinal);

    public static bool ShouldCreateInventoryMovement(bool inventoryTracked) => inventoryTracked;

    public static bool RequiresNewSaleAttempt(string? saleStatus) =>
        string.Equals(saleStatus?.Trim(), "cancelled", StringComparison.OrdinalIgnoreCase);
}

public sealed record MedulaPrescriptionPatientPaymentSummary(
    decimal DebtAmount,
    bool WithDiscount);

public static class MedulaPrescriptionPatientPaymentRules
{
    public static MedulaPrescriptionPatientPaymentSummary Validate(
        decimal patientAmount,
        IReadOnlyList<RetailSalePaymentInput> payments,
        int? patientId)
    {
        ArgumentNullException.ThrowIfNull(payments);
        var withDiscount = payments.Any(payment =>
            payment.Kind == RetailPaymentKind.Discount && payment.Amount > 0m);
        var debtAmount = RetailSaleRules.ValidatePayments(
            patientAmount, payments, withDiscount, patientId);
        return new MedulaPrescriptionPatientPaymentSummary(debtAmount, withDiscount);
    }
}

public sealed record MedulaPrescriptionDebtCollection(
    decimal OwnerAmount,
    decimal RecipientAmount,
    decimal FamilyAmount)
{
    public decimal TotalAmount => Money(OwnerAmount) + Money(RecipientAmount) + Money(FamilyAmount);

    private static decimal Money(decimal value) =>
        decimal.Round(Math.Max(0m, value), 2, MidpointRounding.AwayFromZero);
}

public static class MedulaPrescriptionCheckoutRules
{
    private static readonly RetailPaymentKind[] CollectiblePaymentKinds =
    [
        RetailPaymentKind.Cash,
        RetailPaymentKind.Credit,
        RetailPaymentKind.BankTransfer
    ];

    public static decimal CheckoutTotal(
        decimal prescriptionPatientAmount,
        MedulaPrescriptionDebtCollection debtCollection) =>
        Money(prescriptionPatientAmount) + debtCollection.TotalAmount;

    public static IReadOnlyList<RetailSalePaymentInput> PrescriptionPaymentsOnly(
        decimal prescriptionPatientAmount,
        MedulaPrescriptionDebtCollection debtCollection,
        IReadOnlyList<RetailSalePaymentInput> checkoutPayments)
    {
        ArgumentNullException.ThrowIfNull(debtCollection);
        ArgumentNullException.ThrowIfNull(checkoutPayments);
        var expectedCheckout = CheckoutTotal(prescriptionPatientAmount, debtCollection);
        var actualCheckout = Money(checkoutPayments.Sum(payment => payment.Amount));
        if (actualCheckout != expectedCheckout)
            throw new InvalidOperationException(
                "Ödeme türleri toplamı reçete ve seçilen borç tahsilatı toplamına eşit olmalıdır.");

        var remainingDebt = debtCollection.TotalAmount;
        var amounts = checkoutPayments
            .GroupBy(payment => payment.Kind)
            .ToDictionary(group => group.Key, group => Money(group.Sum(payment => payment.Amount)));
        foreach (var kind in CollectiblePaymentKinds)
        {
            if (remainingDebt <= 0m || !amounts.TryGetValue(kind, out var amount)) continue;
            var collected = Math.Min(amount, remainingDebt);
            amounts[kind] = Money(amount - collected);
            remainingDebt = Money(remainingDebt - collected);
        }
        if (remainingDebt > 0m)
            throw new InvalidOperationException(
                "Ödenecek borç nakit, kart veya havale tutarlarıyla karşılanmalıdır.");

        var prescriptionPayments = amounts
            .Where(pair => pair.Value > 0m)
            .Select(pair => new RetailSalePaymentInput(pair.Key, pair.Value))
            .ToArray();
        if (Money(prescriptionPayments.Sum(payment => payment.Amount)) != Money(prescriptionPatientAmount))
            throw new InvalidOperationException("Reçete ödeme tutarı borç tahsilatından ayrılamadı.");
        return prescriptionPayments;
    }

    private static decimal Money(decimal value) =>
        decimal.Round(Math.Max(0m, value), 2, MidpointRounding.AwayFromZero);
}
