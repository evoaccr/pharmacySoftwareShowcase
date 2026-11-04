using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using pharmacyApp.Modules.Invoices;

namespace pharmacyApp.Modules.Invoices;

public static class UblInvoiceParser
{
    public static EczaciKartInvoiceDocument Parse(string xml, string typeKey)
    {
        var root = XDocument.Parse(xml, LoadOptions.PreserveWhitespace).Root
            ?? throw new InvalidDataException("XML fatura kök öğesi bulunamadı.");
        var type = EczaciKartInvoiceType.FromKey(typeKey);
        var supplier = ReadParty(Child(root, "AccountingSupplierParty") ?? Child(root, "DespatchSupplierParty"));
        var customer = ReadParty(Child(root, "AccountingCustomerParty") ?? Child(root, "DeliveryCustomerParty"));
        var number = Text(root, "ID");
        var ettn = Text(root, "UUID");
        var issueDate = Date(Text(root, "IssueDate"));
        var notes = root.Elements().Where(e => e.Name.LocalName == "Note")
            .Select(e => e.Value.Trim()).Where(value => value.Length > 0).ToArray();
        var currency = Text(root, "DocumentCurrencyCode");
        var invoiceType = Text(root, "InvoiceTypeCode");
        if (string.IsNullOrWhiteSpace(invoiceType)) invoiceType = root.Name.LocalName;
        var scenario = Text(root, "ProfileID");
        var counterparty = type.IsIncoming ? supplier : customer;
        var lines = root.Elements().Where(e => e.Name.LocalName is "InvoiceLine" or "DespatchLine")
            .Select((line, index) => ReadLine(line, index + 1, currency)).ToArray();
        if (lines.Length == 0)
            throw new InvalidDataException(
                $"{(number.Length == 0 ? ettn : number)} XML faturasında içerik satırı bulunamadı.");
        var contentNotes = root.Elements()
            .Where(e => e.Name.LocalName is "InvoiceLine" or "DespatchLine")
            .SelectMany(line => line.Elements().Where(e => e.Name.LocalName == "Note"))
            .Select(e => e.Value.Trim()).Where(value => value.Length > 0);
        var dueDate = Date(Text(root, "DueDate"))
            ?? Date(Text(Desc(root, "PaymentMeans"), "PaymentDueDate"))
            ?? ReadDueDate(issueDate, notes.Concat(contentNotes));
        var taxTotal = Child(root, "TaxTotal");
        var legalTotal = Child(root, "LegalMonetaryTotal");
        var metadata = root.Elements()
            .Where(e => !e.HasElements && !string.IsNullOrWhiteSpace(e.Value))
            .GroupBy(e => e.Name.LocalName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => string.Join(" | ", g.Select(e => e.Value.Trim()).Distinct()), StringComparer.OrdinalIgnoreCase);
        var documentAllowance = Decimal(Text(legalTotal, "AllowanceTotalAmount"))
            ?? Sum(root.Elements()
                .Where(e => e.Name.LocalName == "AllowanceCharge"
                            && !string.Equals(Text(e, "ChargeIndicator"), "true", StringComparison.OrdinalIgnoreCase))
                .Select(e => Decimal(Text(e, "Amount"))));
        if (documentAllowance is not null)
            metadata["DocumentAllowanceAmount"] = documentAllowance.Value.ToString(CultureInfo.InvariantCulture);
        var item = new EczaciKartInvoiceItem(
            type.Key, type.DisplayName, number, ettn,
            issueDate?.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("tr-TR")) ?? string.Empty,
            counterparty.Name, FirstIdentifier(counterparty), scenario,
            Decimal(Text(legalTotal, "PayableAmount"))?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            currency, string.Empty,
            JsonSerializer.Serialize(new { source = "ubl-xml", invoiceType, metadata }),
            true, issueDate is null ? null : new DateTimeOffset(issueDate.Value.ToDateTime(TimeOnly.MinValue)));
        return new EczaciKartInvoiceDocument(
            item, supplier, customer, number, ettn, issueDate, dueDate, scenario, invoiceType, currency,
            Decimal(Text(legalTotal, "LineExtensionAmount")), Decimal(Text(legalTotal, "TaxExclusiveAmount")),
            Decimal(Text(taxTotal, "TaxAmount")), Decimal(Text(legalTotal, "TaxInclusiveAmount")),
            Decimal(Text(legalTotal, "PayableAmount")),
            string.Join(Environment.NewLine, notes),
            metadata, lines, xml);
    }

    private static InvoiceParty ReadParty(XElement? partyContainer)
    {
        var party = Desc(partyContainer, "Party") ?? partyContainer;
        var identifiers = (party?.Descendants().Where(e => e.Name.LocalName == "ID") ?? [])
            .Select(e => (Key: e.Attribute("schemeID")?.Value?.Trim() ?? "Kimlik", Value: e.Value.Trim()))
            .Where(x => x.Value.Length > 0)
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => string.Join(" | ", g.Select(x => x.Value).Distinct()), StringComparer.OrdinalIgnoreCase);
        var address = Desc(party, "PostalAddress");
        var addressParts = new[]
        {
            Text(address, "StreetName"), Text(address, "BuildingName"), Text(address, "BuildingNumber"),
            Text(address, "CitySubdivisionName"), Text(address, "CityName"), Text(address, "PostalZone"),
            Text(Desc(address, "Country"), "Name")
        }.Where(value => !string.IsNullOrWhiteSpace(value));
        var contact = Desc(party, "Contact");
        var partyName = Text(Desc(party, "PartyName"), "Name");
        if (partyName.Length == 0)
        {
            var person = Desc(party, "Person");
            partyName = string.Join(' ', new[]
                {
                    Text(person, "FirstName"), Text(person, "MiddleName"), Text(person, "FamilyName")
                }
                .Where(value => value.Length > 0));
        }
        return new InvoiceParty(
            partyName,
            string.Join(", ", addressParts), Text(contact, "Telephone"), Text(contact, "Telefax"),
            Text(contact, "ElectronicMail"), Text(party, "WebsiteURI"),
            Text(Desc(party, "PartyTaxScheme"), "Name"), Id(identifiers, "VKN"), Id(identifiers, "TCKN"),
            Id(identifiers, "MERSISNO", "MERSIS"), Id(identifiers, "TICARETSICILNO"), Id(identifiers, "GLN"), identifiers);
    }

    private static InvoiceDocumentLine ReadLine(XElement line, int fallback, string documentCurrency)
    {
        var quantity = Child(line, "InvoicedQuantity") ?? Child(line, "DeliveredQuantity");
        var item = Child(line, "Item");
        var price = Child(line, "Price");
        var taxSubtotal = Desc(line, "TaxSubtotal");
        var discountAmount = Sum(line.Elements()
            .Where(e => e.Name.LocalName == "AllowanceCharge"
                        && !string.Equals(Text(e, "ChargeIndicator"), "true", StringComparison.OrdinalIgnoreCase))
            .Select(e => Decimal(Text(e, "Amount"))));
        var values = line.Descendants().Where(e => !e.HasElements && !string.IsNullOrWhiteSpace(e.Value))
            .GroupBy(e => e.Name.LocalName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => string.Join(" | ", g.Select(e => e.Value.Trim()).Distinct()), StringComparer.OrdinalIgnoreCase);
        var lineNo = int.TryParse(Text(line, "ID"), out var parsed) ? parsed : fallback;
        var identifiers = ReadItemIdentifiers(item);
        var sellerCode = identifiers
            .Where(identifier => identifier.Container.Equals("SellersItemIdentification", StringComparison.OrdinalIgnoreCase))
            .Select(identifier => identifier.Value)
            .FirstOrDefault(value => value.Length > 0) ?? string.Empty;
        var additionalIdentifiers = identifiers
            .Where(identifier => identifier.Container.Equals("AdditionalItemIdentification", StringComparison.OrdinalIgnoreCase))
            .Select(identifier => identifier.Value)
            .ToArray();
        var traceabilityCode = additionalIdentifiers
            .Select(ReadGtin)
            .FirstOrDefault(value => value.Length > 0) ?? string.Empty;
        var invoiceBarcode = ReadInvoiceBarcode(identifiers, traceabilityCode);
        var name = Text(item, "Name");
        var description = Text(item, "Description");
        if (name.Length == 0) name = description;
        if (Normalize(name) == Normalize(description)) description = string.Empty;
        return new InvoiceDocumentLine(
            lineNo, sellerCode,
            invoiceBarcode,
            name, description,
            Decimal(quantity?.Value), quantity?.Attribute("unitCode")?.Value ?? string.Empty,
            Decimal(Text(price, "PriceAmount")), Decimal(Text(taxSubtotal, "Percent")),
            Decimal(Text(taxSubtotal, "TaxAmount")), discountAmount,
            Decimal(Text(line, "LineExtensionAmount")),
            Child(line, "LineExtensionAmount")?.Attribute("currencyID")?.Value ?? documentCurrency, values);
    }

    private static XElement? Child(XElement? element, string name) =>
        element?.Elements().FirstOrDefault(e => e.Name.LocalName == name);
    private static XElement? Desc(XElement? element, string name) =>
        element?.Descendants().FirstOrDefault(e => e.Name.LocalName == name);
    private static string Text(XElement? element, string name) => Child(element, name)?.Value.Trim() ?? string.Empty;
    private static DateOnly? Date(string value) => DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    private static decimal? Decimal(string? value) => decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var number) ? number : null;
    private static decimal? Sum(IEnumerable<decimal?> values)
    {
        var present = values.Where(value => value is not null).Select(value => value!.Value).ToArray();
        return present.Length == 0 ? null : present.Sum();
    }

    private static DateOnly? ReadDueDate(DateOnly? issueDate, IEnumerable<string> notes)
    {
        foreach (var note in notes)
        {
            var normalized = Normalize(note);
            var explicitDate = Regex.Match(normalized,
                "(?:VADE\\s*TARIHI|ODEME\\s*TARIHI)[^0-9]{0,30}([0-9]{2}[.]?[0-9]{2}[.]?[0-9]{4})");
            if (explicitDate.Success)
            {
                var digits = Regex.Replace(explicitDate.Groups[1].Value, "[^0-9]", string.Empty);
                if (digits.Length == 8
                    && DateOnly.TryParseExact(digits, "ddMMyyyy", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date)
                    && (issueDate is null || date >= issueDate.Value))
                    return date;
            }

            if (issueDate is null) continue;
            var relativeDays = Regex.Match(normalized, "VADE\\s*GUN(?:U)?[^0-9]{0,15}([0-9]{1,4})");
            if (relativeDays.Success
                && int.TryParse(relativeDays.Groups[1].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var days))
                return issueDate.Value.AddDays(days);
        }
        return null;
    }

    private static string Normalize(string value) => value.ToUpperInvariant()
        .Replace('İ', 'I').Replace('ı', 'I').Replace('Ş', 'S').Replace('Ğ', 'G')
        .Replace('Ü', 'U').Replace('Ö', 'O').Replace('Ç', 'C');
    private static string First(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    private static string ReadGtin(string value)
    {
        var match = Regex.Match(value,
            "(?:[(]\\s*(?:GTIN|01)\\s*[)]|GTIN\\s*[:=])\\s*([0-9]{8,14})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }
    private static string ReadNumericProductCode(string value)
    {
        var match = Regex.Match(value.Trim(), "^[0-9]{8,14}$", RegexOptions.CultureInvariant);
        return match.Success ? match.Value : string.Empty;
    }

    private static IReadOnlyList<ItemIdentifier> ReadItemIdentifiers(XElement? item)
    {
        if (item is null) return [];
        return item.Elements()
            .Where(element => element.Name.LocalName.EndsWith("ItemIdentification", StringComparison.OrdinalIgnoreCase))
            .SelectMany(container => container.DescendantsAndSelf()
                .Where(element => element.Name.LocalName.Equals("ID", StringComparison.OrdinalIgnoreCase))
                .Select(element => new ItemIdentifier(
                    container.Name.LocalName,
                    element.Attribute("schemeID")?.Value?.Trim() ?? string.Empty,
                    element.Value.Trim())))
            .Where(identifier => identifier.Value.Length > 0)
            .ToArray();
    }

    private static string ReadInvoiceBarcode(
        IReadOnlyList<ItemIdentifier> identifiers,
        string traceabilityGtin)
    {
        var candidates = identifiers
            .Select(identifier => new
            {
                Identifier = identifier,
                Numeric = ReadNumericProductCode(identifier.Value),
                TraceGtin = ReadGtin(identifier.Value)
            })
            .Select(candidate => new
            {
                candidate.Identifier,
                Value = First(candidate.Numeric, candidate.TraceGtin),
                Priority = IdentifierPriority(candidate.Identifier, candidate.TraceGtin.Length > 0)
            })
            .Where(candidate => candidate.Value.Length > 0)
            .OrderBy(candidate => candidate.Priority)
            .ThenByDescending(candidate => IsValidGtin(candidate.Value))
            .ToArray();

        var valid = candidates.FirstOrDefault(candidate => IsValidGtin(candidate.Value));
        if (valid is not null) return NormalizeGtinBarcode(valid.Value);
        if (candidates.Length > 0) return NormalizeGtinBarcode(candidates[0].Value);
        return NormalizeGtinBarcode(traceabilityGtin);
    }

    private static int IdentifierPriority(ItemIdentifier identifier, bool containsTraceability)
    {
        var scheme = Normalize(identifier.SchemeId);
        if (scheme.Contains("GTIN", StringComparison.Ordinal)
            || scheme.Contains("EAN", StringComparison.Ordinal)
            || scheme.Contains("BARKOD", StringComparison.Ordinal)
            || scheme.Contains("BARCODE", StringComparison.Ordinal))
            return 0;
        if (containsTraceability) return 1;
        return identifier.Container switch
        {
            var value when value.Equals("StandardItemIdentification", StringComparison.OrdinalIgnoreCase) => 2,
            var value when value.Equals("ManufacturersItemIdentification", StringComparison.OrdinalIgnoreCase) => 3,
            var value when value.Equals("BuyersItemIdentification", StringComparison.OrdinalIgnoreCase) => 4,
            var value when value.Equals("SellersItemIdentification", StringComparison.OrdinalIgnoreCase) => 5,
            _ => 6
        };
    }

    private static bool IsValidGtin(string value)
    {
        if (value.Length is not (8 or 12 or 13 or 14) || value.Any(character => !char.IsDigit(character)))
            return false;
        var sum = 0;
        var weight = 3;
        for (var index = value.Length - 2; index >= 0; index--)
        {
            sum += (value[index] - '0') * weight;
            weight = weight == 3 ? 1 : 3;
        }
        return (10 - sum % 10) % 10 == value[^1] - '0';
    }

    private static string NormalizeGtinBarcode(string value)
    {
        var numeric = ReadNumericProductCode(value);
        if (numeric.Length == 14 && numeric[0] == '0') return numeric[1..];
        return numeric;
    }

    private sealed record ItemIdentifier(string Container, string SchemeId, string Value);
    private static string Id(IReadOnlyDictionary<string, string> ids, params string[] keys) =>
        keys.Select(key => ids.FirstOrDefault(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    private static string FirstIdentifier(InvoiceParty party) => !string.IsNullOrWhiteSpace(party.Vkn) ? party.Vkn : party.Tckn;
}
