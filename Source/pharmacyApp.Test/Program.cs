using pharmacyApp.Modules.Products;
using pharmacyApp.Modules.Sales;
using pharmacyApp.Modules.Medula;
using pharmacyApp.Modules.Invoices;
using pharmacyApp.Modules.Its;
using System.Net;

var tests = new (string Name, Action Run)[]
{
    ("eczaci-kart-page-mapping", EczaciKartPageMappingUsesPortalFields),
    ("eczaci-kart-detail-table", EczaciKartDetailTablePreservesHeadersAndRows),
    ("eczaci-kart-wholesaler-values", EczaciKartWholesalerValuesAreParsedByMeaning),
    ("invoice-medication-structure", InvoiceMedicationStructureSeparatesServices),
    ("invoice-medication-traceability", InvoiceMedicationTraceabilityIdentifiesMedicine),
    ("invoice-individual-traceability-codes", InvoiceIndividualTraceabilityCodesAreParsed),
    ("invoice-incomplete-traceability", InvoiceIncompleteTraceabilityIsRejected),
    ("invoice-preserved-traceability", InvoicePreservedTraceabilitySatisfiesQuantity),
    ("invoice-pts-package-serials", InvoicePtsPackageParsesEverySerial),
    ("invoice-pts-exact-match", InvoicePtsPackageRequiresExactInvoiceReference),
    ("invoice-mixed-stock-plan", InvoiceMixedStockPlanIncludesQrAndBarcodeProducts),
    ("invoice-its-stock-actions", InvoiceItsStockActionsBuildMalAlimAndMalIadeWithoutNetwork),
    ("invoice-partial-refund-selection", InvoicePartialRefundSelectionGroupsIndividualQrCodes),
    ("invoice-partial-refund-its-send", InvoicePartialRefundCanSendMalIadeOutsideDebugMode),
    ("ubl-compact-invoice-fields", UblCompactInvoiceFieldsAreParsed),
    ("ubl-selcuk-invoice-content", UblSelcukInvoiceContentIsParsed),
    ("ubl-universal-item-identifiers", UblUniversalItemIdentifiersPreferInvoiceBarcode),
    ("invoice-legacy-merge-ranking", InvoiceLegacyMergeUsesStrongestHeader),
    ("invoice-legacy-merge-stable-tie", InvoiceLegacyMergeConsumesEquivalentSavedDuplicates),
    ("invoice-legacy-merge-ambiguous-state", InvoiceLegacyMergeRejectsMixedSavedState),
    ("populated", PopulatedPayloadRetainsKnownAndUnknownFields),
    ("empty", EmptyPayloadIsDistinguished),
    ("null", NullPayloadIsDistinguished),
    ("unknown", UnknownNestedKeysRemainVisible),
    ("malformed", MalformedPayloadIsReported),
    ("sut-optional-ui", SutOptionalUiStateIsDerivedFromUsableData),
    ("sut-html", SutHtmlIsConvertedToReadableText),
    ("warning-icon", WarningIconIsMatchedByHeaderAndUrl),
    ("catalog-copy-and-stock-range", CatalogCopyAndStockRangeFormattingIsStable),
    ("product-analysis-calculations", ProductAnalysisCalculationsFollowNinetyDayAndShelfStockRules),
    ("retail-sale-cart", RetailSaleCartGroupsAndTotalsItems),
    ("retail-payment-balancer", RetailPaymentBalancerMaintainsExactTotal),
    ("retail-money-parts", RetailMoneyPartsComposeAndSplitAmounts),
    ("retail-turkish-patient-search", RetailTurkishPatientSearchNormalizesDottedAndDotlessI),
    ("retail-cancellation-lifetime", RetailCancellationLifetimeIsIdempotent),
    ("retail-refund-patient-resolution", RetailRefundPatientResolutionDetectsMixedSales),
    ("retail-refund-payment-rules", RetailRefundPaymentRulesAcceptOnlySupportedExactPayments),
    ("retail-qr-normalizer", RetailQrNormalizerAcceptsScannerFormats),
    ("retail-movement-address", RetailMovementAddressUsesPatientRecordId),
    ("retail-debt-rules", RetailDebtRequiresPatientAndReturnsAmount)
    ,("retail-qr-discount-allocation", RetailQrDiscountAllocationPreservesExactTotal)
    ,("retail-refund-debt-reduction", RetailRefundDebtReductionIsBounded)
    ,("consignment-sale-contract", ConsignmentSaleContractUsesReservedStockAndUnifiedType)
    ,("consignment-settlement-payments", ConsignmentSettlementShowsOnlyOutstandingCollection)
    ,("consignment-cancellation-sales-list", ConsignmentCancellationIsASeparateEntryTransaction)
    ,("its-transfer-request", ItsTransferRequestUsesMalDevirContractWithoutNetwork)
    ,("its-acceptance-request", ItsAcceptanceRequestUsesMalAlimContractWithoutNetwork)
    ,("its-return-request", ItsReturnRequestUsesMalIadeContractWithoutNetwork)
    ,("its-deactivation-requests", ItsDeactivationRequestsUseDocumentedContractsWithoutNetwork)
    ,("its-reference-lists", ItsReferenceListsUseDocumentedContractsWithoutNetwork)
    ,("its-sale-response-code", ItsSaleResultUsesOfficialResponseCodeList)
    ,("its-acceptance-network-failure", ItsAcceptanceConnectionFailureIsSafelyClassified)
    ,("its-takas-active-qr", ItsTakasQrParsesAllGs1Fields)
    ,("its-status-query-contract", ItsStatusQueryUsesDocumentedContract)
    ,("its-status-query-independent-sources", ItsStatusQueryKeepsSuccessfulLocalResult)
    ,("its-status-query-transient-retry", ItsStatusQueryRetriesTransientFailure)
    ,("its-retail-multi-qr-request", ItsRetailRequestContainsEveryQr)
    ,("its-retail-success-lifecycle", ItsRetailSuccessCompletesRemoteBeforeLocal)
    ,("its-retail-definitely-not-sent", ItsRetailDefinitelyNotSentNeverChangesLocalStock)
    ,("its-retail-ambiguous-response", ItsRetailAmbiguousResponseRequiresReconciliation)
    ,("medula-page-classifier", MedulaPageClassifierUsesRenderedLoginFormInsteadOfLoginJspUrl)
    ,("medula-prescription-field-map", MedulaPrescriptionFieldMapMatchesLiveControls)
    ,("medula-date-normalizer", MedulaDatesAlwaysUseFourDigitYears)
    ,("medula-subscreen-safety", MedulaSubScreenActionsAreStrictlyAllowlisted)
    ,("medula-subscreen-destination", MedulaSubScreensRequireBackendDestinationProof)
    ,("medula-subscreen-time-budget", MedulaSubScreenFailureWaitIsBounded)
    ,("medula-editable-popup-layout", MedulaEditablePopupLayoutGroupsDoseFactors)
    ,("medula-remote-screen-refinement", MedulaRemoteScreensAreRefined)
    ,("medula-browser-warning", MedulaBrowserWarningsRemainExactAndPreferAlerts)
    ,("medula-warning-bridge", MedulaWarningBridgeNeverReopensNativeDialogs)
    ,("medula-save-guard", MedulaSaveGuardRequiresDefinitiveRemoteState)
    ,("medula-doctor-reconciliation", MedulaDoctorSpecialtyRespectsDefaultRules)
    ,("medula-normalized-doctor-schema", MedulaDoctorImportUsesNormalizedBranchRelation)
    ,("medula-prescription-list-refresh-policy", MedulaPrescriptionListRefreshHasExplicitTriggers)
    ,("medula-prescription-normalization", MedulaPrescriptionNormalizationMapsInvoiceAndFinalization)
    ,("medula-saved-prescription-header", MedulaSavedPrescriptionHeaderMapsCompactSummary)
    ,("medula-prescription-number-persistence", MedulaPrescriptionNumberIsPreservedForPersistence)
    ,("medula-electronic-prescription-access-route", MedulaElectronicPrescriptionUsesCorrectQueryRoute)
    ,("medula-prescription-sale-calculation", MedulaPrescriptionSaleCalculationSeparatesReceivables)
    ,("medula-drug-quantity-reduction", MedulaDrugQuantityReductionPreservesBothValues)
    ,("medula-prescription-message-deduplication", MedulaPrescriptionMessagesPreferDetailedBarcodeErrors)
    ,("medula-electronic-prescription-drug-notes", MedulaElectronicPrescriptionDrugNotesKeepOnlyMeaningfulText)
    ,("medula-report-ingredient-matching", MedulaReportIngredientColorsRequirePrescriptionMatch)
    ,("medula-report-row-alignment", MedulaReportRowsAlignMatchedGroupsAndSeparateUnmatchedRows)
    ,("medula-database-transient-retry", MedulaDatabaseTransientRetryRecognizesStreamFailures)
    ,("medula-prescription-patient-discount", MedulaPrescriptionPatientDiscountIsAValidAllocation)
    ,("medula-prescription-sale-cancellation", MedulaPrescriptionSaleCancellationPreservesInventoryTruth)
    ,("medula-prescription-history", MedulaPrescriptionHistoryDescribesChangesAndDeletedStatus)
    ,("medula-prescription-codebook", MedulaPrescriptionCodebookUsesStableStorageCodes)
    ,("medula-insurance-type-resolution", MedulaInsuranceTypeResolutionUsesMedulaEvidence)
    ,("medula-qr-finalization-result", MedulaQrFinalizationResultUsesRenderedPageEvidence)
    ,("medula-qr-finalization-cancellation-result", MedulaQrFinalizationCancellationUsesRenderedPageEvidence)
    ,("loyal-patient-monthly-continuity", LoyalPatientMonthlyContinuityIsRecognized)
    ,("loyal-patient-same-day-burst", LoyalPatientSameDayBurstIsRejected)
    ,("loyal-patient-long-history-gap", LoyalPatientLongHistoryToleratesOneMissedInterval)
    ,("loyal-patient-lapsed-history", LoyalPatientLapsedHistoryIsRejected)
    ,("loyal-patient-half-monthly-threshold", LoyalPatientHalfMonthlyThresholdMeansEveryTwoMonths)
    ,("loyal-patient-medication-history-parser", LoyalPatientMedicationHistoryUsesBarcode)
    ,("loyal-patient-medication-history-without-inline-barcode", LoyalPatientMedicationHistoryKeepsNameWithoutInlineBarcode)
    ,("loyal-patient-medication-barcode-resolution", LoyalPatientMedicationBarcodeResolutionUsesPrescription)
    ,("loyal-patient-report-history-parser", LoyalPatientReportHistoryUsesRequestedColumns)
    ,("loyal-patient-strikes-remove-on-third", LoyalPatientStrikesRemoveOnThirdFailure)
    ,("loyal-patient-strikes-reset-on-loyal", LoyalPatientStrikesResetWhenQualified)
    ,("loyal-patient-default-tolerance", LoyalPatientDefaultToleranceAcceptsOrdinaryGaps)
    ,("loyal-patient-configurable-strictness", LoyalPatientStrictnessCanBeConfigured)
    ,("loyal-patient-report-supply-ratio", LoyalPatientReportSupplyRatioMatchesPrescriptionNumbers)
    ,("loyal-patient-report-threshold-strict", LoyalPatientReportSupplyThresholdIsStrict)
    ,("loyal-patient-report-sample-delay", LoyalPatientReportSampleDelayHonorsJitter)
    ,("loyal-patient-report-population-estimate", LoyalPatientReportSampleEstimatesPopulation)
    ,("loyal-patient-report-medication-link", LoyalPatientReportSupplyUsesMedicationHistoryLink)
    ,("loyal-patient-diagnosis-is-not-report", LoyalPatientDiagnosisOnlyRowIsNotReportEvidence)
    ,("loyal-patient-hybrid-family-inheritance", LoyalPatientHybridFamilyInheritanceWorksWithoutHistory)
    ,("loyal-patient-hybrid-keeps-mechanism2", LoyalPatientHybridKeepsMechanism2Positive)
    ,("loyal-patient-hybrid-mechanism1-fallback", LoyalPatientHybridFallsBackAfterMechanism2Rejection)
    ,("loyal-patient-hybrid-report-retention", LoyalPatientHybridRetainsPastReportQualification)
};

static void ItsAcceptanceRequestUsesMalAlimContractWithoutNetwork()
{
    var product = new ItsQrProduct("08699536090115", "2674000056097596", "262028", new DateOnly(2031, 2, 28));
    var request = ItsAcceptanceRequestFactory.Create([product]);
    Assert(request.Products.Count == 1, "Mal Alım request contains the serialized product");

    var handler = new FakeItsHandler();
    using var client = new HttpClient(handler);
    var result = new ItsApiClient(client).SendAcceptanceAsync(
        ItsEnvironment.Test,
        new ItsCredentials("offline-user", "offline-password"),
        request,
        CancellationToken.None).GetAwaiter().GetResult();

    Assert(handler.Calls.Count == 2, "offline handler receives token and acceptance calls");
    Assert(handler.Calls[1].Path == "/common/app/accept/", "Mal Alım uses the acceptance endpoint");
    Assert(handler.Calls[1].Authorization == "Bearer offline-token", "acceptance call uses the acquired bearer token");
    using var acceptanceJson = System.Text.Json.JsonDocument.Parse(handler.Calls[1].Body);
    Assert(acceptanceJson.RootElement.GetProperty("productList").GetArrayLength() == 1,
        "serialized Mal Alım request uses the documented productList field");
    Assert(result.IsSuccessful && result.NotificationId == "4242", "successful acceptance response is parsed");
}

static void ItsReturnRequestUsesMalIadeContractWithoutNetwork()
{
    var expiredProduct = new ItsQrProduct(
        "08699536090115", "RETURN-SERIAL", "RETURN-LOT", new DateOnly(2024, 2, 29));
    var request = ItsReturnRequestFactory.Create("8680001000012", [expiredProduct]);
    Assert(request.ToGln == "8680001000012" && request.Products.Count == 1,
        "Mal İade supports the documented expired-product return use case");

    var handler = new FakeItsHandler();
    using var client = new HttpClient(handler);
    var result = new ItsApiClient(client).SendReturnAsync(
        ItsEnvironment.Test,
        new ItsCredentials("offline-user", "offline-password"),
        request,
        CancellationToken.None).GetAwaiter().GetResult();

    Assert(handler.Calls.Count == 2 && handler.Calls[1].Path == "/common/app/return/",
        "Mal İade uses the documented return endpoint");
    using var requestJson = System.Text.Json.JsonDocument.Parse(handler.Calls[1].Body);
    Assert(requestJson.RootElement.GetProperty("togln").GetString() == "8680001000012" &&
           requestJson.RootElement.GetProperty("productList")[0].GetProperty("xd").GetString() == "2024-02-29",
        "Mal İade serializes destination and product identity fields");
    Assert(result.IsSuccessful, "Mal İade success response is parsed");
}

static void ItsDeactivationRequestsUseDocumentedContractsWithoutNetwork()
{
    var expiredProduct = new ItsQrProduct(
        "08699536090115", "DEACT-SERIAL", "DEACT-LOT", new DateOnly(2024, 2, 29));
    var deactivate = ItsDeactivationRequestFactory.Create(
        "8680001000012", ItsDeactivationReason.Expiry, "Miadı dolan ürün", [expiredProduct],
        new DateOnly(2026, 8, 30), "TUTANAK-42");
    var cancel = ItsDeactivationRequestFactory.CreateCancellation(
        "8680001000012", ItsDeactivationReason.Expiry, "Hatalı deaktivasyon", [expiredProduct]);
    Assert(deactivate.NotificationType == "D" && deactivate.ReasonCode == "40" &&
           deactivate.Document.DocumentDate == "2026-08-30" && deactivate.Document.DocumentNumber == "TUTANAK-42",
        "deactivation request uses dt D, reason code 40 and documented document fields");
    Assert(cancel.NotificationType == "C" && cancel.ReasonCode == "40",
        "deactivation cancellation request uses dt C and retains the reason code");

    var deactivationHandler = new FakeItsHandler();
    using (var client = new HttpClient(deactivationHandler))
    {
        _ = new ItsApiClient(client).SendDeactivationAsync(
            ItsEnvironment.Test, new ItsCredentials("offline-user", "offline-password"),
            deactivate, CancellationToken.None).GetAwaiter().GetResult();
    }
    Assert(deactivationHandler.Calls[1].Path == "/common/app/deactivation/",
        "deactivation uses the documented endpoint");

    var cancellationHandler = new FakeItsHandler();
    using (var client = new HttpClient(cancellationHandler))
    {
        _ = new ItsApiClient(client).SendDeactivationCancellationAsync(
            ItsEnvironment.Test, new ItsCredentials("offline-user", "offline-password"),
            cancel, CancellationToken.None).GetAwaiter().GetResult();
    }
    Assert(cancellationHandler.Calls[1].Path == "/common/app/deactivationcancel/",
        "deactivation cancellation uses the documented endpoint");
}

static void ItsReferenceListsUseDocumentedContractsWithoutNetwork()
{
    var handler = new FakeItsReferenceHandler();
    using var client = new HttpClient(handler);
    var api = new ItsApiClient(client);
    var credentials = new ItsCredentials("offline-user", "offline-password");
    var responseCodes = api.GetResponseCodesAsync(
        ItsEnvironment.Test, credentials, CancellationToken.None).GetAwaiter().GetResult();
    var drugs = api.GetDrugsAsync(
        ItsEnvironment.Test, credentials, true, CancellationToken.None).GetAwaiter().GetResult();

    Assert(handler.Calls.Select(call => call.Path).SequenceEqual([
            "/token/app/token/", "/reference/app/errorcode/",
            "/token/app/token/", "/reference/app/drug/"
        ]),
        "reference lists use the documented response-code and drug endpoints");
    Assert(responseCodes.Count == 1 && responseCodes[0].Code == "60014" &&
           responseCodes[0].Description == "Ürün eczane stokunda",
        "response code list parses code, message and description");
    Assert(drugs.Count == 1 && drugs[0].DrugName == "Örnek İlaç" && drugs[0].Active && drugs[0].Imported,
        "drug list parses product, manufacturer, active and imported fields");
    using var drugRequest = System.Text.Json.JsonDocument.Parse(handler.Calls[3].Body);
    Assert(drugRequest.RootElement.GetProperty("getAll").GetBoolean(),
        "drug list includeInactive option maps to documented getAll=true");
}

static void ItsSaleResultUsesOfficialResponseCodeList()
{
    var product = new ItsQrProduct(
        "08699536090115", "SOLD-SERIAL", "SOLD-LOT", new DateOnly(2031, 2, 28));
    var request = ItsPharmacyRequestFactory.Create(
        ItsPharmacyOperation.RetailSale, "8680001000012", [product]);
    var handler = new FakeItsSaleErrorHandler();
    using var client = new HttpClient(handler);
    var result = new ItsApiClient(client).SendNotificationAsync(
        ItsEnvironment.Test, ItsPharmacyOperation.RetailSale,
        new ItsCredentials("offline-user", "offline-password"), request,
        CancellationToken.None).GetAwaiter().GetResult();

    Assert(handler.Calls.Select(call => call.Path).SequenceEqual([
            "/token/app/token/", "/prescription/app/pharmacysale/",
            "/token/app/token/", "/reference/app/errorcode/"
        ]),
        "sale result resolves non-success codes through the response-code service");
    Assert(result.Products[0].ResultMessage == "Ürün daha önce satılmış" &&
           result.Products[0].ResultDisplay.Contains("10204", StringComparison.Ordinal),
        "sale result exposes the official response message to callers");
}

static void ItsAcceptanceConnectionFailureIsSafelyClassified()
{
    var product = new ItsQrProduct("08699536090115", "2674000056097596", "262028", new DateOnly(2031, 2, 28));
    var request = ItsAcceptanceRequestFactory.Create([product]);
    using var client = new HttpClient(new FailingAcceptanceHandler());

    AssertThrows<ItsNotificationNotSentException>(
        () => new ItsApiClient(client).SendAcceptanceAsync(
            ItsEnvironment.Test,
            new ItsCredentials("offline-user", "offline-password"),
            request,
            CancellationToken.None).GetAwaiter().GetResult(),
        "definite Mal Alım connection failure is classified as not sent");
}

static void InvoiceItsStockActionsBuildMalAlimAndMalIadeWithoutNetwork()
{
    const string qr = "0108699536090115212674000056097596173102281026262028";
    var movements = new[]
    {
        new InvoiceStockMovementItem(1, qr, true, 12.50m),
        new InvoiceStockMovementItem(2, "8690000000000", false, 4.25m)
    };

    var acceptance = InvoiceItsNotificationPlanner.Build(true, "8680001000012", movements);
    Assert(acceptance.OperationCode == "ACCEPTANCE"
           && acceptance.OperationDisplay == "Mal Alım"
           && acceptance.ProductCount == 1
           && acceptance.Request is ItsAcceptanceRequest,
        "invoice save prepares one serialized Mal Alım product and excludes barcode-only stock");

    var invoiceReturn = InvoiceItsNotificationPlanner.Build(false, "8680001000012", movements);
    Assert(invoiceReturn.OperationCode == "RETURN"
           && invoiceReturn.OperationDisplay == "Mal İade"
           && invoiceReturn.ProductCount == 1
           && invoiceReturn.Request is ItsReturnRequest { ToGln: "8680001000012" },
        "invoice refund prepares one serialized Mal İade product for the depot GLN");
}

static void InvoicePartialRefundSelectionGroupsIndividualQrCodes()
{
    var first = new PartialInvoiceRefundQrRow
    {
        InvoiceUuid = "invoice-a", IssueDate = new DateOnly(2026, 8, 30), SenderName = "Depot A",
        Barcode = "8699578091026", ProductName = "Medicine", QrCode = "qr-a",
        ExpiryDate = new DateOnly(2030, 12, 31)
    };
    var second = new PartialInvoiceRefundQrRow
    {
        InvoiceUuid = "invoice-b", IssueDate = new DateOnly(2026, 8, 31), SenderName = "Depot B",
        Barcode = "8699578091026", ProductName = "Medicine", QrCode = "qr-b",
        ExpiryDate = new DateOnly(2030, 12, 31)
    };
    var cart = new PartialInvoiceRefundCart();
    var group = cart.Add(first);
    cart.Add(second);

    Assert(group.QrCodes.Count == 2 && group.Quantity == 2 && cart.QrCount == 2,
        "partial invoice refund cart groups only QR codes explicitly added by the operator");
    Assert(cart.InvoiceCount == 2,
        "one partial refund cart can retain links to multiple invoices");
    cart.Remove(first);
    Assert(cart.QrCount == 1 && group.Quantity == 1,
        "individual QR codes can be removed from the partial refund cart");
}

static void InvoicePartialRefundCanSendMalIadeOutsideDebugMode()
{
    const string qr = "0108699536090115212674000056097596173102281026262028";
    var plan = InvoiceItsNotificationPlanner.Build(
        false,
        "8680001000012",
        [new InvoiceStockMovementItem(1, qr, true, null)]);
    var client = new FakeInvoiceItsReturnClient();
    var sender = new InvoiceItsReturnSender(
        client,
        () => new InvoiceItsReturnConfiguration(
            ItsEnvironment.Production,
            new ItsCredentials("configured-user", "configured-password")));

    var execution = sender.SendAsync([plan], CancellationToken.None).GetAwaiter().GetResult();

    Assert(client.CallCount == 1 && client.Environment == ItsEnvironment.Production,
        "non-debug partial refund sends one Mal İade request to the production İTS client");
    Assert(client.Request is { ToGln: "8680001000012", Products.Count: 1 },
        "non-debug partial refund sends the selected QR to its invoice depot");
    Assert(execution.NotificationCount == 1 && execution.NotificationIds.SequenceEqual(["partial-return-1"]),
        "successful Mal İade notification identity is returned to the transaction workflow");
}

static void ItsTakasQrParsesAllGs1Fields()
{
    const string qr = "01086995780910262100000024114179172912311026126004";
    Assert(ItsQrParser.TryParse(qr, out var product, out var error), $"active Takas QR parses: {error}");
    Assert(product is not null
           && product.Gtin == "08699578091026"
           && product.SerialNumber == "00000024114179"
           && product.BatchNumber == "26126004"
           && product.ExpiryDate == new DateOnly(2029, 12, 31),
        "active Takas QR retains GTIN, serial, batch and expiry fields");

    const string sixteenCharacterSerialQr = "0108699536090115212674000056097596173102281026262028";
    Assert(ItsQrParser.TryParse(sixteenCharacterSerialQr, out var standardProduct, out _)
           && standardProduct?.SerialNumber == "2674000056097596"
           && standardProduct.BatchNumber == "26262028",
        "existing 16-character separator-free ITS QR layout remains supported");

    Assert(!ItsQrParser.TryParse(
            "0108699536090115212674000056097669-173102281026262028",
            out _,
            out _),
        "malformed separator-free QR remains rejected");
}

static void ItsStatusQueryUsesDocumentedContract()
{
    var product = new ItsQrProduct("08699578091026", "00000024114179", "26126004", new DateOnly(2029, 12, 31));
    var handler = new FakeItsStatusHandler();
    using var client = new HttpClient(handler);
    var result = new ItsApiClient(client).QueryProductStatusAsync(
        ItsEnvironment.Production,
        new ItsCredentials("offline-user", "offline-password"),
        ItsStatusQueryRequestFactory.Create(product),
        CancellationToken.None).GetAwaiter().GetResult();

    Assert(handler.Calls.Count == 4, "status query and response-code enrichment each use authenticated read-only calls");
    Assert(handler.Calls[1].Path == "/reference/app/check_status/", "status query uses the documented endpoint");
    Assert(handler.Calls[1].Authorization == "Bearer offline-token", "status query uses bearer authentication");
    using var requestJson = System.Text.Json.JsonDocument.Parse(handler.Calls[1].Body);
    Assert(requestJson.RootElement.GetProperty("productList").GetArrayLength() == 1,
        "status query serializes the documented productList");
    Assert(result.HasUsableResponse && result.Products[0].Gln1 == "8680001000012" &&
           result.Products[0].ResultCode == "60014", "GLN1, GLN2 and uc are parsed from the response");
    Assert(result.Products[0].ResultMessage == "Ürün eczane stokunda",
        "status query function exposes the official response-code message");

    var owned = ItsStatusOwnershipClassifier.Classify(result.Products[0], "8680001000012");
    Assert(owned.State == ItsOwnershipState.RegisteredToThisPharmacy && owned.IsRegisteredToThisPharmacy,
        "empty GLN2 and matching GLN1 identify locally owned stock");
    var outgoing = ItsStatusOwnershipClassifier.Classify(
        result.Products[0] with
        {
            Gln1 = "8680001000036", Gln2 = "8680001000012", ResultCode = "60012"
        },
        "8680001000012");
    Assert(outgoing.State == ItsOwnershipState.OutgoingTransfer && outgoing.DestinationGln == "8680001000036",
        "60012 identifies an outgoing transfer and GLN1 as destination");
    var sold = ItsStatusOwnershipClassifier.Classify(
        result.Products[0] with
        {
            Gln1 = "8680001000012", Gln2 = string.Empty, ResultCode = "60004"
        },
        "8680001000012");
    Assert(sold.State == ItsOwnershipState.SoldToEndConsumer && !sold.IsRegisteredToThisPharmacy,
        "60004 identifies a completed sale even when GLN1 still names the selling pharmacy");
    var cancellableSale = ItsStatusOwnershipClassifier.Classify(
        result.Products[0] with { ResultCode = "60011" },
        "8680001000012");
    Assert(cancellableSale.State == ItsOwnershipState.SoldToEndConsumer &&
           cancellableSale.Summary.Contains("Satış İptal", StringComparison.Ordinal),
        "60011 identifies a sale which the querying pharmacy can cancel");
    var currentSold = ItsStatusOwnershipClassifier.Classify(
        result.Products[0] with { ResultCode = "60018" },
        "8680001000012");
    Assert(currentSold.State == ItsOwnershipState.SoldToEndConsumer && !currentSold.IsRegisteredToThisPharmacy,
        "60018 identifies the current İTS sold state even when GLN1 remains the pharmacy");
    var futureUnknown = ItsStatusOwnershipClassifier.Classify(
        result.Products[0] with { ResultCode = "69999" },
        "8680001000012");
    Assert(futureUnknown.State == ItsOwnershipState.Unknown && !futureUnknown.IsRegisteredToThisPharmacy,
        "unknown non-success status codes never fall back to an unsafe ownership claim");
}

static void ItsStatusQueryKeepsSuccessfulLocalResult()
{
    var local = LocalQrStatusFormatter.NotFound();
    var service = new ItsProductStatusQueryService(
        new FakeLocalProductStatusSource(local),
        new FailingItsStatusClient());
    var report = service.QueryAsync(
        "01086995780910262100000024114179172912311026126004",
        ItsEnvironment.Production,
        new ItsCredentials("offline-user", "offline-password"),
        "8680001000012",
        CancellationToken.None).GetAwaiter().GetResult();

    Assert(object.ReferenceEquals(report.LocalStatus, local), "local status remains available when İTS is offline");
    Assert(report.ItsAssessment is null && report.ItsError?.Contains("offline", StringComparison.OrdinalIgnoreCase) == true,
        "İTS connection failure is reported independently");
}

static void ItsStatusQueryRetriesTransientFailure()
{
    var product = new ItsQrProduct("08699578091026", "00000024114179", "26126004", new DateOnly(2029, 12, 31));
    var handler = new FakeItsStatusHandler { RemainingStatusFailures = 1 };
    using var client = new HttpClient(handler);
    var result = new ItsApiClient(client).QueryProductStatusAsync(
        ItsEnvironment.Production,
        new ItsCredentials("offline-user", "offline-password"),
        ItsStatusQueryRequestFactory.Create(product),
        CancellationToken.None).GetAwaiter().GetResult();

    Assert(result.HasUsableResponse && result.Products[0].ResultCode == "60014",
        "read-only status query succeeds after a transient server response");
    Assert(handler.Calls.Count == 6 &&
           handler.Calls.Select(call => call.Path).SequenceEqual([
               "/token/app/token/", "/reference/app/check_status/",
               "/token/app/token/", "/reference/app/check_status/",
               "/token/app/token/", "/reference/app/errorcode/"
           ]),
        "transient retry repeats the status query and enriches its successful result afterward");
}

static void ItsRetailRequestContainsEveryQr()
{
    var first = new ItsQrProduct("08699536090115", "SERIAL1", "LOT1", new DateOnly(2031, 2, 28));
    var second = first with { SerialNumber = "SERIAL2", BatchNumber = "LOT2" };
    var request = ItsPharmacyRequestFactory.Create(
        ItsPharmacyOperation.RetailSale,
        "8680001000012",
        [first, second]);
    Assert(request.NotificationType == "S" && request.Products.Count == 2,
        "shared pharmacy request factory retains every QR in one sale notification");
}

static void ItsRetailSuccessCompletesRemoteBeforeLocal()
{
    var store = new FakeRetailItsStore();
    var client = new FakeRetailItsClient(request => new ItsNotificationResult(
        200,
        "ok",
        "N-42",
        null,
        null,
        request.Products.Select(product => new ItsProductResult(product.Gtin, product.SerialNumber, "00000")).ToArray()));
    var service = CreateRetailItsService(client, store);
    var localCalled = false;
    var result = service.ExecuteAsync(
        RetailItsOperationKind.Sale,
        ["(01)08699536090115(21)SERIAL1(17)310228(10)LOT1"],
        "admin",
        _ =>
        {
            Assert(store.Events.SequenceEqual(["prepared", "remote_confirmed"]),
                "local stock mutation starts only after remote confirmation is durable");
            localCalled = true;
            return Task.FromResult(42);
        },
        CancellationToken.None).GetAwaiter().GetResult();

    Assert(localCalled && result.LocalRecordId == 42 && result.NotificationId == "N-42",
        "successful İTS response completes the local sale and returns both identifiers");
    Assert(store.Events.SequenceEqual(["prepared", "remote_confirmed", "local_completed"]),
        "successful retail lifecycle reaches local_completed");
}

static void ItsRetailDefinitelyNotSentNeverChangesLocalStock()
{
    var store = new FakeRetailItsStore();
    var client = new FakeRetailItsClient(_ => throw new ItsNotificationNotSentException(
        "offline",
        new HttpRequestException("offline")));
    var service = CreateRetailItsService(client, store);
    var localCalled = false;
    AssertThrows<RetailItsDefinitelyNotSentException>(() => service.ExecuteAsync(
        RetailItsOperationKind.Sale,
        ["(01)08699536090115(21)SERIAL1(17)310228(10)LOT1"],
        "admin",
        _ =>
        {
            localCalled = true;
            return Task.FromResult(1);
        },
        CancellationToken.None).GetAwaiter().GetResult(),
        "definitely-not-sent İTS failure is exposed as safely retryable");
    Assert(!localCalled && store.Events.SequenceEqual(["prepared", "definitely_not_sent"]),
        "definitely-not-sent failure never changes local stock and releases through its terminal state");
}

static void ItsRetailAmbiguousResponseRequiresReconciliation()
{
    var store = new FakeRetailItsStore();
    var client = new FakeRetailItsClient(request => new ItsNotificationResult(
        200,
        "partial",
        "N-43",
        null,
        null,
        [new ItsProductResult(request.Products[0].Gtin, request.Products[0].SerialNumber, "00000")]));
    var service = CreateRetailItsService(client, store);
    var localCalled = false;
    AssertThrows<RetailItsReconciliationRequiredException>(() => service.ExecuteAsync(
        RetailItsOperationKind.Sale,
        [
            "(01)08699536090115(21)SERIAL1(17)310228(10)LOT1",
            "(01)08699536090115(21)SERIAL2(17)310228(10)LOT2"
        ],
        "admin",
        _ =>
        {
            localCalled = true;
            return Task.FromResult(1);
        },
        CancellationToken.None).GetAwaiter().GetResult(),
        "partial product result requires reconciliation");
    Assert(!localCalled && store.Events.SequenceEqual(["prepared", "reconciliation_required"]),
        "ambiguous response never changes local stock or enters a retryable terminal state");
}

static RetailItsOrchestrationService CreateRetailItsService(
    IItsPharmacyNotificationClient client,
    IRetailItsOperationStore store) => new(
        client,
        store,
        () => new RetailItsConfiguration(
            ItsEnvironment.Production,
            new ItsCredentials("user", "password"),
            "8680001000012"));

static void ItsTransferRequestUsesMalDevirContractWithoutNetwork()
{
    var product = new ItsQrProduct("08699536090115", "2674000056097596", "262028", new DateOnly(2031, 2, 28));
    var request = ItsTransferRequestFactory.Create("8680001000012", [product]);
    Assert(request.ToGln == "8680001000012" && request.Products.Count == 1,
        "Mal Devir request contains destination GLN and serialized product");

    var handler = new FakeItsHandler();
    using var client = new HttpClient(handler);
    var result = new ItsApiClient(client).SendTransferAsync(
        ItsEnvironment.Test,
        new ItsCredentials("offline-user", "offline-password"),
        request,
        CancellationToken.None).GetAwaiter().GetResult();

    Assert(handler.Calls.Count == 2, "offline handler receives token and transfer calls");
    Assert(handler.Calls[1].Path == "/common/app/transfer/", "Mal Devir uses the transfer endpoint");
    Assert(handler.Calls[1].Authorization == "Bearer offline-token", "transfer call uses the acquired bearer token");
    using var transferJson = System.Text.Json.JsonDocument.Parse(handler.Calls[1].Body);
    Assert(transferJson.RootElement.GetProperty("togln").GetString() == "8680001000012",
        "serialized request uses the documented togln field");
    Assert(result.IsSuccessful && result.NotificationId == "4242", "successful transfer response is parsed");
}

static void MedulaPrescriptionHistoryDescribesChangesAndDeletedStatus()
{
    var item = new MedulaPrescriptionListItem(
        "123", "28/08/2026", "28/08/2026", "A", "B", "4A", "Sonlandırılmadı",
        "K", "Evet", string.Empty, string.Empty, "1", "20260801", "local", 1, 0)
    {
        IsDeleted = true
    };
    Assert(item.QrCodeStatusDisplay == "Silindi", "deleted prescriptions display an explicit deleted QR status");

    var description = MedulaPrescriptionHistoryFormatter.Describe(
        "UPDATE",
        "{\"patient_total_payment\":15.5,\"qr_status_code\":2}",
        "{\"patient_total_payment\":10.0,\"qr_status_code\":1}");
    Assert(description.Contains("Hasta Ödemesi: 10.0 → 15.5", StringComparison.Ordinal),
        "history exposes changed payment values in readable form");
    Assert(description.Contains("Karekod Durumu", StringComparison.Ordinal),
        "history exposes changed Medula status values");
}

static void MedulaPrescriptionCodebookUsesStableStorageCodes()
{
    Assert(MedulaPrescriptionCodebook.InvoiceTypeCode("A Grubu", "2") == 2,
        "the Medula invoice variant preserves the content-return code");
    Assert(MedulaPrescriptionCodebook.InvoiceDisplayName(3) == "A Grubu Mevzuattan İade",
        "invoice codes resolve to an exact group and return status");
    Assert(MedulaPrescriptionCodebook.InvoiceGroupName(5) == "B Grubu",
        "invoice group display is derived from the coded variant");
    Assert(MedulaPrescriptionCodebook.QrStatusCode("Sonlandırıldı") == MedulaPrescriptionCodebook.QrFinalized,
        "finalized QR status is stored as a stable code");
    Assert(MedulaPrescriptionCodebook.PrescriptionTypeCode("Kırmızı") == 2
           && MedulaPrescriptionCodebook.PrescriptionSubtypeCode("Ayaktan") == 1
           && MedulaPrescriptionCodebook.ProvisionTypeCode("Trafik Kazası") == 2,
        "finite prescription categories are normalized to small integer codes");
}

static void MedulaQrFinalizationCancellationUsesRenderedPageEvidence()
{
    static MedulaPrescriptionPageResult Result(string pageText) => new(
        true, true, "Reçete", string.Empty, string.Empty, pageText);

    Assert(MedulaQrFinalizationCancellationResultDetector.IsConfirmed(Result("Karekodlu ilaç var")),
        "the live unfinalized Medula status proves finalization cancellation");
    Assert(MedulaQrFinalizationCancellationResultDetector.IsConfirmed(Result("Reçete Sonlandırılmadı")),
        "the explicit unfinalized message proves finalization cancellation");
    Assert(!MedulaQrFinalizationCancellationResultDetector.IsConfirmed(Result("Reçete Sonlandırıldı")),
        "a still-finalized prescription never completes the cancellation locally");
}

static void MedulaPrescriptionSaleCancellationPreservesInventoryTruth()
{
    Assert(MedulaPrescriptionSaleLifecycleRules.ShouldCreateInventoryMovement(true),
        "a QR proven to exist in local stock creates the prescription movement");
    Assert(!MedulaPrescriptionSaleLifecycleRules.ShouldCreateInventoryMovement(false),
        "a Medula-only QR never invents a local stock movement");
    Assert(MedulaPrescriptionSaleLifecycleRules.NormalizeItsCancellationQr(
            "01086995690907172165000023667372-173104301026220276A")
        == "01086995690907172165000023667372173104301026220276A",
        "Medula's visual ITS separator still matches the original serialized QR");
    Assert(MedulaPrescriptionSaleLifecycleRules.RequiresNewSaleAttempt("cancelled"),
        "a fully refunded prescription sale starts a new auditable attempt");
    Assert(!MedulaPrescriptionSaleLifecycleRules.RequiresNewSaleAttempt("pending")
        && !MedulaPrescriptionSaleLifecycleRules.RequiresNewSaleAttempt("medula_reported")
        && !MedulaPrescriptionSaleLifecycleRules.RequiresNewSaleAttempt("completed"),
        "an active or completed sale is never duplicated");
}

static void MedulaPrescriptionPatientDiscountIsAValidAllocation()
{
    var summary = MedulaPrescriptionPatientPaymentRules.Validate(
        100m,
        [
            new RetailSalePaymentInput(RetailPaymentKind.Cash, 75m),
            new RetailSalePaymentInput(RetailPaymentKind.Discount, 25m)
        ],
        patientId: null);
    Assert(summary.WithDiscount, "prescription patient allocation records the discount flag");
    Assert(summary.DebtAmount == 0m, "discount does not create patient debt");
}

static void MedulaQrFinalizationResultUsesRenderedPageEvidence()
{
    static MedulaPrescriptionPageResult Result(
        string message = "",
        string pageText = "",
        IReadOnlyDictionary<string, string>? fields = null) => new(
            true, true, "Reçete", string.Empty, message, pageText, FieldValues: fields);

    Assert(MedulaQrFinalizationResultDetector.IsConfirmed(Result(
            pageText: "Reçete Sonlandırıldı")),
        "the final prescription page is definitive even when the click response has no success banner");
    Assert(MedulaQrFinalizationResultDetector.IsConfirmed(Result(
            fields: new Dictionary<string, string> { ["medula:qr-status"] = "Sonlandırıldı" })),
        "a scraped finalized QR status is definitive");
    Assert(!MedulaQrFinalizationResultDetector.IsConfirmed(Result(
            pageText: "Reçete Sonlandırılmadı")),
        "an unfinalized prescription is never mistaken for a completed finalization");
}

static void MedulaPrescriptionNormalizationMapsInvoiceAndFinalization()
{
    Assert(MedulaPrescriptionListTypes.ResolveInvoiceType("-", "2") == "A Grubu",
        "invoice type falls back to the Medula list selection");
    Assert(MedulaPrescriptionListTypes.ResolveInvoiceType("B Grubu", "1") == "B Grubu",
        "a usable scraped invoice type wins over the list fallback");
    Assert(MedulaPrescriptionListTypes.IsItsFinalized(" Sonlandırıldı "),
        "Sonlandırıldı maps to finalized");
    Assert(!MedulaPrescriptionListTypes.IsItsFinalized("Sonlandırılmadı"),
        "Sonlandırılmadı never maps to finalized");
}

static void MedulaDrugQuantityReductionPreservesBothValues()
{
    var before = new[]
    {
        new MedulaDrugEntry { Barcode = "8690000000001", Name = "ALEVE", Quantity = "2" }
    };
    var after = new[]
    {
        new MedulaDrugEntry { Barcode = "8690000000001", Name = "ALEVE", Quantity = "1" }
    };

    MedulaDrugQuantityAdjustments.AnnotateReductions(before, after);
    Assert(after[0].Quantity == "1" && after[0].OriginalQuantity == "2",
        "the Medula-adjusted quantity is retained while the originally entered quantity remains visible");

    var unchanged = new[]
    {
        new MedulaDrugEntry { Barcode = "8690000000002", Quantity = "2" }
    };
    MedulaDrugQuantityAdjustments.AnnotateReductions(unchanged, unchanged);
    Assert(unchanged[0].OriginalQuantity.Length == 0,
        "unchanged quantities do not receive a misleading previous-value marker");
}

static void MedulaPrescriptionMessagesPreferDetailedBarcodeErrors()
{
    const string summary = "8699673057149 barkodlu ilacın ayaktan raporsuz ödenmesi için gerekli şartlar sağlanamıyor.";
    const string detailed = "8699673057149 barkodlu ilacın ayaktan raporsuz ödenmesi için gerekli şartlar sağlanamıyor. Sebep(ler)i ; * reçete tanısı raporsuz SUT kuralı ile uyumsuz, reçete uyarı kodları uyumsuz ( 246 ), reçeteyi yazan doktorun branşı uyumsuz, (358733) * reçeteyi yazan doktorun branşı uyumsuz, (358735)";
    const string differentBarcode = "8699673057150 barkodlu ilacın ayaktan raporsuz ödenmesi için gerekli şartlar sağlanamıyor.";

    Assert(MedulaPrescriptionMessageRules.AreDuplicateErrors(summary, detailed),
        "a detailed barcode validation and its repeated summary are one frontend error");
    Assert(!MedulaPrescriptionMessageRules.AreDuplicateErrors(summary, differentBarcode),
        "matching error wording for another barcode remains a distinct error");
    Assert(MedulaPrescriptionMessageRules.PreferDetailedError(summary, detailed) == detailed,
        "the detailed Medula reason list is retained instead of the repeated summary");

    var drugs = new[]
    {
        new MedulaDrugEntry { Barcode = "8699673057149", Name = "Test medicine" },
        new MedulaDrugEntry { Barcode = "8699673057150", Name = "Other medicine" }
    };
    MedulaPrescriptionMessageRules.ApplyBarcodeErrors(drugs, [summary, detailed]);
    Assert(drugs[0].ErrorText == detailed && drugs[1].ErrorText.Length == 0,
        "the preferred full validation is attached only to the affected barcode row");
    MedulaPrescriptionMessageRules.ApplyBarcodeErrors(drugs, []);
    Assert(drugs.All(drug => drug.ErrorText.Length == 0),
        "barcode indicators clear when the backend no longer reports an error");
}

static void MedulaElectronicPrescriptionDrugNotesKeepOnlyMeaningfulText()
{
    Assert(MedulaElectronicPrescriptionDrugNotes.ImportantDiagnosis(
            "Teşhis/Tanı - 83547654 Protokolüne ait reçeteler").Length == 0,
        "numeric protocol-only medication text is hidden");
    Assert(MedulaElectronicPrescriptionDrugNotes.ImportantDiagnosis(
            "Teşhis/Tanı - A-417323-2-001-A-040926-00000266 Protokolüne ait reçeteler").Length == 0,
        "compound protocol-only medication text is hidden");
    Assert(MedulaElectronicPrescriptionDrugNotes.ImportantDiagnosis(
            "Teşhis/Tanı - SABAH AÇ KARNA") == "Teşhis/Tanı - SABAH AÇ KARNA",
        "meaningful per-medication directions are retained and normalized");
    Assert(MedulaElectronicPrescriptionDrugNotes.ImportantDiagnosis("   ").Length == 0,
        "empty detail rows remain hidden");
}

static void MedulaReportIngredientColorsRequirePrescriptionMatch()
{
    Assert(MedulaReportIngredientMatching.HasPrescriptionMedication(
            " 123-ABC ", ["123ABC", "999"]),
        "an active ingredient receives a color when a prescription medicine has the same SGK code");
    Assert(!MedulaReportIngredientMatching.HasPrescriptionMedication(
            "777", ["123ABC", "999"]),
        "an unrelated active ingredient remains uncolored");
    Assert(!MedulaReportIngredientMatching.HasPrescriptionMedication(
            "", ["123ABC"]),
        "an empty SGK code never receives a generated color");
}

static void MedulaReportRowsAlignMatchedGroupsAndSeparateUnmatchedRows()
{
    var slots = MedulaReportRowAlignment.Build(
        ["ORANGE", "BLUE", "UNPAIRED-REPORT"],
        ["ORANGE", "ORANGE", "BLUE", "UNPAIRED-MEDICATION"]);

    Assert(slots.Count == 5,
        "aligned report tables contain balanced matched rows, one separator and unpaired rows");
    Assert(slots[0] == new MedulaReportRowAlignmentSlot(0, 0, "ORANGE", false)
        && slots[1] == new MedulaReportRowAlignmentSlot(null, 1, "ORANGE", false),
        "a blank report row balances a larger matching medication color group");
    Assert(slots[2] == new MedulaReportRowAlignmentSlot(1, 2, "BLUE", false),
        "the next matching color group remains aligned on both sides");
    Assert(slots[3].IsSeparator
        && slots[3].ReportRowIndex is null
        && slots[3].MedicationRowIndex is null,
        "exactly one padding row separates matched and unmatched groups");
    Assert(slots[4] == new MedulaReportRowAlignmentSlot(2, 3, string.Empty, false),
        "uncolored report and medication rows remain at the bottom");
}

static void MedulaDatabaseTransientRetryRecognizesStreamFailures()
{
    var streamFailure = new InvalidOperationException(
        "Exception while reading from stream",
        new IOException("The transport connection was interrupted."));
    Assert(MedulaDatabaseRetryPolicy.IsTransient(streamFailure),
        "stream-backed database failures are retried as transient transport interruptions");
    Assert(!MedulaDatabaseRetryPolicy.IsTransient(
            new InvalidOperationException("foreign key constraint failed")),
        "deterministic schema/constraint failures are not retried");
    Assert(MedulaDatabaseRetryPolicy.MaxAttempts == 3,
        "pending Medula sale creation has a bounded retry count");
}

static void MedulaSavedPrescriptionHeaderMapsCompactSummary()
{
    var fields = MedulaPrescriptionHeaderParser.Normalize(
        new Dictionary<string, string>
        {
            ["medula:prescription-no"] = "0",
            ["medula:qr-status"] = "Karekodlu ilaç yok",
            ["medula:coverage"] = "Fatura Türü",
            ["medula:patient-type"] = "Normal",
            ["medula:invoice-type"] = "-"
        },
        "Reçete No: 3PU0QGL Karekod Durumu: Karekodlu ilaç var " +
        "Kapsam: 4A Sigortalı (S.S.K.) Emekli Fat.Türü: A Grubu");

    Assert(fields["medula:prescription-no"] == "3PU0QGL",
        "the compact header supplies the real prescription number instead of zero");
    Assert(fields["medula:qr-status"] == "Karekodlu ilaç var",
        "the compact header supplies the QR status");
    Assert(fields["medula:coverage"] == "4A Sigortalı (S.S.K.)",
        "coverage is not confused with the invoice label");
    Assert(fields["medula:patient-type"] == "Emekli"
        && fields["medula:insurance-type"] == "Emekli",
        "the member type between coverage and invoice type is preserved");
    Assert(fields["medula:invoice-type"] == "A Grubu",
        "the abbreviated Fat.Türü label is parsed");

    var noisyInvoice = MedulaPrescriptionHeaderParser.Normalize(
        new Dictionary<string, string>
        {
            ["medula:invoice-type"] = "Fatura Türü: A Grubu",
            ["medula:coverage"] = "Fatura Türü"
        },
        string.Empty);
    Assert(noisyInvoice["medula:invoice-type"] == "A Grubu"
        && !noisyInvoice.ContainsKey("medula:coverage"),
        "invoice captions are stripped from values and can never leak into coverage");

    var source = new MedulaPrescriptionListItem(
        "3PU0QGL", "", "", "", "", "4A Sigortalı (S.S.K.)",
        "Karekodlu ilaç var", "K", "Evet", "", "", "2", "202609", "local", 1, 0);
    var restored = MedulaPrescriptionHeaderParser.Normalize(
        new Dictionary<string, string> { ["medula:prescription-no"] = "0" },
        string.Empty,
        source);
    Assert(restored["medula:prescription-no"] == "3PU0QGL",
        "a known source-list number replaces an invalid scrape so refresh never queries prescription zero");

    var disambiguated = MedulaPrescriptionHeaderParser.Normalize(
        new Dictionary<string, string>
        {
            ["medula:coverage"] = "Fatura Türü",
            ["medula:patient-type"] = "Kapsam"
        },
        string.Empty);
    Assert(!disambiguated.ContainsKey("medula:coverage")
        && !disambiguated.ContainsKey("medula:patient-type"),
        "header captions are never retained as prescription data");
    Assert(MedulaPrescriptionHeaderParser.HasAuthoritativeSummary(fields, "3PU0QGL"),
        "the complete saved header can update the top summary immediately");
}

static void MedulaPrescriptionNumberIsPreservedForPersistence()
{
    var source = new MedulaPrescriptionListItem(
        "3PU0QGL", "", "01/09/2026", "Test", "Hasta", "4A Sigortalı (S.S.K.)",
        "Sonlandırılmadı", "K", "Evet", "", "", "2", "202609", "local", 1, 0);
    var persistedFields = MedulaPrescriptionHeaderParser.Normalize(
        new Dictionary<string, string> { ["medula:prescription-no"] = "0" },
        string.Empty,
        source);
    var preservedNumber = persistedFields["medula:prescription-no"];
    Assert(preservedNumber == "3PU0QGL",
        "an incomplete page scrape cannot erase the known Medula prescription number before persistence");
    Assert(MedulaPrescriptionSaveGuard.IsConfirmed(new MedulaPrescriptionPageResult(
            true, true, "Reçete", "", "", "",
            FieldValues: new Dictionary<string, string>
            {
                ["medula:prescription-no"] = preservedNumber
            },
            SavedSummary: new MedulaSavedPrescriptionSummary(
                "Reçete Tutarları",
                [new MedulaPrescriptionPaymentField("Toplam Tutar", "1,00")],
                ["Kaydet"]))),
        "the preserved real number remains usable by the definitive save guard and Reçete Sorgu flow");
}

static void MedulaElectronicPrescriptionUsesCorrectQueryRoute()
{
    Assert(MedulaPrescriptionAccessPolicy.Resolve(
            false, string.Empty, "2Q8TERI")
        == MedulaPrescriptionAccessRoute.ElectronicPrescriptionQuery,
        "an e-prescription that has not reached the priced saved screen reopens through E-Reçete Sorgu");
    Assert(MedulaPrescriptionAccessPolicy.Resolve(
            true, "3PU0QGL", "2Q8TERI")
        == MedulaPrescriptionAccessRoute.PrescriptionQuery,
        "after the priced saved screen opens, the Medula prescription reopens through Reçete Sorgu");
    Assert(MedulaPrescriptionAccessPolicy.Resolve(
            false, string.Empty, string.Empty)
        == MedulaPrescriptionAccessRoute.PrescriptionEntry,
        "an ordinary unsaved paper draft remains on Reçete Giriş");
    Assert(MedulaPrescriptionAccessPolicy.Resolve(
            true, string.Empty, "2Q8TERI")
        == MedulaPrescriptionAccessRoute.PrescriptionQuery,
        "a known saved e-prescription is never incorrectly retried through E-Reçete Sorgu");
}

static void MedulaPrescriptionSaleCalculationSeparatesReceivables()
{
    var employee = MedulaPrescriptionSaleCalculator.Calculate(
        "Çalışan", 12.50m, 100m, 4m, 6m, 20m);
    Assert(employee.PatientAmount == 12.50m
        && employee.PrimaryInstitutionAmount == 90m
        && employee.SecondaryInstitutionAmount == 0m
        && employee.OwedAmount == 10m
        && employee.TotalAmount == 102.50m,
        "employee institution receivable excludes cash prescription and examination contributions");

    var retired = MedulaPrescriptionSaleCalculator.Calculate(
        "Emekli", 12.50m, 100m, 4m, 6m, 20m);
    Assert(retired.PatientAmount == 12.50m
        && retired.PrimaryInstitutionAmount == 100m
        && retired.SecondaryInstitutionAmount == 20m
        && retired.OwedAmount == 10m
        && retired.TotalAmount == 132.50m,
        "retired medication contribution is tracked as a separate institution receivable");

    AssertThrows<InvalidOperationException>(
        () => MedulaPrescriptionSaleCalculator.Calculate("Bilinmiyor", 1m, 2m, 0m, 0m, 0m),
        "unknown insurance types are never guessed");

    var collectedDebt = new MedulaPrescriptionDebtCollection(10m, 5m, 5m);
    var prescriptionPayments = MedulaPrescriptionCheckoutRules.PrescriptionPaymentsOnly(
        100m,
        collectedDebt,
        [
            new RetailSalePaymentInput(RetailPaymentKind.Cash, 70m),
            new RetailSalePaymentInput(RetailPaymentKind.Credit, 50m)
        ]);
    Assert(MedulaPrescriptionCheckoutRules.CheckoutTotal(100m, collectedDebt) == 120m
        && prescriptionPayments.Sum(payment => payment.Amount) == 100m
        && prescriptionPayments.Single(payment => payment.Kind == RetailPaymentKind.Cash).Amount == 50m,
        "selected existing debt increases checkout but is excluded from prescription sale payments");
    AssertThrows<InvalidOperationException>(
        () => MedulaPrescriptionCheckoutRules.PrescriptionPaymentsOnly(
            100m,
            collectedDebt,
            [new RetailSalePaymentInput(RetailPaymentKind.Debt, 120m)]),
        "existing debt collection must be covered by cash, card, or bank transfer");
}

static void MedulaInsuranceTypeResolutionUsesMedulaEvidence()
{
    Assert(MedulaInsuranceTypeResolver.Resolve(
            "", "4C Sigortalı (Emekli Sandığı) Emekli Fat.Türü: B Grubu",
            0m, 3.76m, 0m, 0m) == "Emekli",
        "the actual value immediately before Fat.Türü wins over coverage wording");
    Assert(MedulaInsuranceTypeResolver.Resolve(
            "", "Kapsam: 4C Sigortalı (Emekli Sandığı)",
            2m, 0m, 1m, 0m) == "Çalışan",
        "cash-only contributions provide a safe employee fallback");
    Assert(MedulaInsuranceTypeResolver.Resolve(
            "", "Kapsam: 4A Sigortalı", 0m, 0m, 0m, 0m) == string.Empty,
        "coverage alone never guesses employee or retired status");
}

var selectedTests = args.Length == 0
    ? tests
    : tests.Where(test => args.Contains(test.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
if (args.Length > 0 && selectedTests.Length != args.Distinct(StringComparer.OrdinalIgnoreCase).Count())
    throw new InvalidOperationException("One or more requested tests do not exist.");
foreach (var test in selectedTests)
{
    test.Run();
    Console.WriteLine($"PASS {test.Name}");
}

static void EczaciKartPageMappingUsesPortalFields()
{
    const string payload = """
        {
          "totalPages": 2,
          "content": [{
            "documentId": "FTR-42",
            "documentUuid": "uuid-42",
            "documentIssueDate": "2026-08-22T10:30:00",
            "sourceTitle": "Gönderen Eczane",
            "sourceId": "1234567890",
            "documentProfile": "TICARIFATURA",
            "payableAmount": 125.50,
            "currencyCode": "TRY",
            "responseCode": "KABUL"
          }]
        }
        """;
    var rows = EczaciKartInvoiceMapper.MapPage(EczaciKartInvoiceType.FromKey("incoming-invoice"), payload);
    Assert(EczaciKartInvoiceMapper.ReadTotalPages(payload) == 2, "portal page count is preserved");
    Assert(rows.Count == 1
        && rows[0].DocumentNumber == "FTR-42"
        && rows[0].CounterpartyName == "Gönderen Eczane"
        && rows[0].CounterpartyTaxId == "1234567890"
        && rows[0].Amount == "125.50"
        && rows[0].Currency == "TRY"
        && rows[0].Status == "KABUL",
        "incoming portal fields map to the unified document row");
}

static void EczaciKartDetailTablePreservesHeadersAndRows()
{
    var table = EczaciKartInvoiceDetailTable.Parse(
        """{"headers":["Sıra No","Mal Hizmet","Miktar",null],"rows":[["1","Test Ürün","2 Adet","10,00 TRY"],["2","İkinci Ürün","1 Adet"]]}""");
    Assert(table.Headers.SequenceEqual(["Sıra No", "Mal Hizmet", "Miktar", "Sütun 4"]),
        "detail headers are retained and unnamed columns receive stable labels");
    Assert(table.Rows.Count == 2
        && table.Rows[0].Cells.SequenceEqual(["1", "Test Ürün", "2 Adet", "10,00 TRY"])
        && table.Rows[1].Cells.SequenceEqual(["2", "İkinci Ürün", "1 Adet", ""]),
        "detail rows are preserved and padded to the full table width");
}

static void InvoiceMedicationStructureSeparatesServices()
{
    Assert(!InvoiceMedicationClassifier.HasPharmacyPricingFields(
            ["Sıra No", "Mal Hizmet", "Miktar", "Birim Fiyat", "KDV Oranı", "Mal Hizmet Tutarı"]),
        "ordinary service invoice columns are not medication evidence");
    Assert(InvoiceMedicationClassifier.HasPharmacyPricingFields(
            ["Sira No", "Mal Hizmet", "Miktar", "Depocu Fiyati", "MF Isk. Sonrasi Fiyat", "Net Tutar"]),
        "pharmaceutical depot pricing columns identify a medication-related invoice");
    Assert(InvoiceMedicationClassifier.HasPharmacyPricingFields(
            ["ÜRÜN ADI", "ECZ.İSK%", "PERAKENDE SATIŞ FİYATI(TL)"]),
        "pharmacy discount and retail-price columns identify alternate wholesaler templates");
}

static void InvoiceMedicationTraceabilityIdentifiesMedicine()
{
    Assert(InvoiceMedicationClassifier.HasItsTraceabilityData(
            ["(GTIN)8699578593612(BN)26293012(SN)20000021030553(XD)280331"]),
        "İTS GTIN and serial fields identify a medicine even when the local barcode catalog is incomplete");
    Assert(!InvoiceMedicationClassifier.HasItsTraceabilityData(["8699512007113"]),
        "a plain commercial barcode is not sufficient traceability evidence");
}

static void InvoiceIndividualTraceabilityCodesAreParsed()
{
    var result = InvoiceTraceabilityParser.Parse(new Dictionary<string, string>
    {
        ["ID"] = "1 | 8699717010093 | "
                 + "(GTIN)8699717010093(BN)252894(SN)212336019(XD)301027 | "
                 + "(GTIN)08699717010093(BN)252894(SN)212336022(XD)301027"
    });

    Assert(result.CandidateCount == 2 && result.Codes.Count == 2 && result.IsComplete,
        "every complete Eczacı Kart traceability block becomes one individual package");
    Assert(result.Codes[0].QrCode == "(01)08699717010093(17)301027(10)252894(21)212336019"
           && result.Codes[0].Barcode == "8699717010093"
           && result.Codes[0].ExpiryDate == new DateOnly(2030, 10, 27),
        "individual package fields use the canonical GS1 form consumed by stock movements");

    var monthEndResult = InvoiceTraceabilityParser.Parse([
        "(GTIN)8699717010093(BN)252894(SN)212336023(XD)301000"
    ]);
    Assert(monthEndResult.IsComplete
           && monthEndResult.Codes.Single().ExpiryDate == new DateOnly(2030, 10, 31),
        "a traceability expiry day of 00 follows ITS convention and resolves to month end");

    var alternateOrder = InvoiceTraceabilityParser.Parse([
        "(GTIN)8699717010093(SN)SERIAL-3(BN)LOT-3(XD)301027"
    ]);
    Assert(alternateOrder.IsComplete && alternateOrder.Codes.Single().SerialNumber == "SERIAL-3"
                                     && alternateOrder.Codes.Single().BatchNumber == "LOT-3",
        "traceability fields are parsed by marker instead of depending on one vendor-specific order");

    var repeated = InvoiceTraceabilityParser.Parse([
        "(GTIN)8699717010093(BN)LOT-4(SN)SERIAL-4(XD)301027 | "
        + "(GTIN)8699717010093(BN)LOT-4(SN)SERIAL-4(XD)301027"
    ]);
    Assert(repeated.IsComplete && repeated.CandidateCount == 1 && repeated.Codes.Count == 1,
        "the same package repeated in multiple UBL identifiers is staged once without a false missing-QR error");

    var gs1 = InvoiceTraceabilityParser.Parse([
        "(01)08699717010093(17)301027(10)LOT-GS1(21)SERIAL-GS1"
    ]);
    Assert(gs1.IsComplete && gs1.Codes.Single().SerialNumber == "SERIAL-GS1"
                          && gs1.Codes.Single().BatchNumber == "LOT-GS1",
        "standard GS1 application-identifier text is accepted independently of depot-specific labels");

    var aliases = InvoiceTraceabilityParser.Parse([
        "GTIN=8699717010093 LOT=LOT-ALIAS SERIAL=SERIAL-ALIAS EXP=301027"
    ]);
    Assert(aliases.IsComplete && aliases.Codes.Single().SerialNumber == "SERIAL-ALIAS",
        "common traceability label aliases are parsed without a depot-specific template");
}

static void InvoiceIncompleteTraceabilityIsRejected()
{
    var result = InvoiceTraceabilityParser.Parse([
        "(GTIN)8699717010093(BN)252894(SN)(XD)301027 | (GTIN)8699717010093(BN)252894"
    ]);

    Assert(result.CandidateCount == 2 && result.Codes.Count == 0 && !result.IsComplete
           && result.Errors.Count > 0,
        "partial or malformed traceability blocks are retained as a blocking condition instead of invented stock");
}

static void InvoicePreservedTraceabilitySatisfiesQuantity()
{
    var sparseXml = InvoiceTraceabilityParser.Parse([
        "(GTIN)08680881014017(BN)73126(SN)00000000000002704732(XD)280731"
    ]);
    Assert(sparseXml.Codes.Count == 1,
        "the Bursa-style sparse XML fixture contains one representative QR");
    Assert(InvoiceTraceabilityParser.BuildTrackingWarning(4m, sparseXml, 4).Length == 0,
        "four preserved source QR rows satisfy a four-unit Bursa invoice line");
    Assert(InvoiceTraceabilityParser.BuildTrackingWarning(4m, sparseXml, 1).Contains("saklanan tekil karekod sayısı 1"),
        "a genuinely incomplete Bursa line remains explicitly marked");

    var noXmlTraceability = InvoiceTraceabilityParser.Parse(Array.Empty<string>());
    Assert(InvoiceTraceabilityParser.BuildTrackingWarning(2m, noXmlTraceability, 2).Length == 0,
        "QR rows imported from a prior pharmacy system remain valid without XML traceability text");
}

static void InvoicePtsPackageParsesEverySerial()
{
    const string xml = """
        <transfer>
          <documentNumber>TK02026018243606</documentNumber>
          <productList>
            <product><gtin>08699525124807</gtin><bn>A34545</bn><xd>2029-05-31</xd>
              <snList><sn>94010024106787</sn><sn>94010024106788</sn><sn>94010024106789</sn></snList>
            </product>
          </productList>
        </transfer>
        """;
    using var output = new MemoryStream();
    using (var archive = new System.IO.Compression.ZipArchive(
               output, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
    {
        var entry = archive.CreateEntry("package.xml");
        using var writer = new StreamWriter(entry.Open());
        writer.Write(xml);
    }
    var parsed = ItsPtsPackageParser.Parse(output.ToArray());
    Assert(parsed.Codes.Count == 3
           && parsed.Codes.Select(code => code.SerialNumber).Distinct().Count() == 3,
        "one PTS product group yields every individual serial rather than one representative QR");
    Assert(parsed.Codes.All(code => code.Gtin == "08699525124807" && code.BatchNumber == "A34545"),
        "shared PTS product fields are retained for each serial");
}

static void InvoicePtsPackageRequiresExactInvoiceReference()
{
    var matching = ItsPtsPackageParser.Parse(System.Text.Encoding.UTF8.GetBytes(
        "<transfer><documentNumber>TK02026018243606</documentNumber></transfer>"));
    Assert(ItsPtsPackageParser.MatchesInvoice(matching, "TK02026018243606", "different-uuid"),
        "the exact normalized invoice number links a PTS package");
    Assert(!ItsPtsPackageParser.MatchesInvoice(matching, "TK02026018243607", "different-uuid"),
        "a neighboring invoice number is never accepted as a PTS match");
}

static void InvoiceMixedStockPlanIncludesQrAndBarcodeProducts()
{
    var movements = InvoiceStockMovementPlanner.Build([
        new InvoiceStockLine(1, 1, "Serili ilaç", "8699717010093", 2m, 10m, 0m, 20m,
            "QR", "", ["qr-1", "qr-2"]),
        new InvoiceStockLine(2, 2, "Barkodlu ürün", "8690000000001", 3m, 5m, 0m, 15m,
            "BARCODE", "", []),
        new InvoiceStockLine(3, 3, "Elektrik hizmeti", "", 1m, 50m, 0m, 50m,
            "NONE", "", [])
    ]);

    Assert(movements.Count == 5
           && movements.Count(movement => movement.IsSerialized) == 2
           && movements.Count(movement => !movement.IsSerialized) == 3
           && movements.All(movement => movement.LineId != 3),
        "mixed invoices create individual QR and per-unit barcode movements while non-stock services are skipped");
}

static void EczaciKartWholesalerValuesAreParsedByMeaning()
{
    var item = new EczaciKartInvoiceItem(
        "incoming-invoice", "Gelen Fatura", "TEST-1", "uuid-1", "25.08.2026",
        "Test Depo", "1234567890", "TICARIFATURA", "79493", "TRY", string.Empty, "{}");
    var table = new EczaciKartInvoiceDetailTable(
        ["SIRA NO", "ÜRÜN ADI", "MİKTAR", "BİRİM SATIŞ FİYATI(TL)", "KDV%", "TUTAR(TL)"],
        [
            new EczaciKartInvoiceDetailRow(["1", "Test İlaç", "5+5+1", "162,28", "1", "787,06"]),
            new EczaciKartInvoiceDetailRow(["Vade tarihi : 23.12.2026 |794,93", "", "", "", "", ""])
        ]);

    var document = EczaciKartInvoiceDocumentParser.Parse(item, table, string.Empty);

    Assert(document.Lines.Count == 1, "summary/footer rows are not imported as invoice contents");
    Assert(document.Lines[0].Quantity == 11m, "compound wholesaler quantity is summed");
    Assert(document.Lines[0].Unit == string.Empty && document.Lines[0].UnitPrice == 162.28m,
        $"unit price is not mistaken for the unit code (unit='{document.Lines[0].Unit}', price={document.Lines[0].UnitPrice})");
    Assert(document.Lines[0].Description == string.Empty,
        "missing descriptions are not duplicated from the product name");
    Assert(document.LineExtensionAmount == 787.06m && document.TaxAmount == 7.8706m
        && document.PayableAmount == 794.93m,
        "line total, KDV and minor-unit portal payable amount retain decimal scale");
    Assert(document.DueDate == new DateOnly(2026, 12, 23),
        "due date is recovered from the wholesaler footer");
}

static void UblCompactInvoiceFieldsAreParsed()
{
    const string xml = """
        <Invoice>
          <ID>INV-42</ID><UUID>11111111-2222-3333-4444-555555555555</UUID>
          <IssueDate>2026-08-25</IssueDate>
          <Note>ÖDEME TARİHİ : 23.11.2026</Note>
          <DocumentCurrencyCode>TRY</DocumentCurrencyCode>
          <AccountingSupplierParty><Party>
            <PartyIdentification><ID schemeID="VKN">1234567890</ID></PartyIdentification>
            <PartyName><Name>Test Ecza Deposu</Name></PartyName>
          </Party></AccountingSupplierParty>
          <AccountingCustomerParty><Party>
            <PartyIdentification><ID schemeID="TCKN">12345678901</ID></PartyIdentification>
            <Person><FirstName>Fikret</FirstName><FamilyName>Acar</FamilyName></Person>
          </Party></AccountingCustomerParty>
          <TaxTotal><TaxAmount>7.87</TaxAmount></TaxTotal>
          <AllowanceCharge><ChargeIndicator>false</ChargeIndicator><Amount>4.00</Amount></AllowanceCharge>
          <LegalMonetaryTotal>
            <LineExtensionAmount>787.06</LineExtensionAmount>
            <TaxExclusiveAmount>787.06</TaxExclusiveAmount>
            <TaxInclusiveAmount>794.93</TaxInclusiveAmount>
            <AllowanceTotalAmount>4.00</AllowanceTotalAmount>
            <PayableAmount>794.93</PayableAmount>
          </LegalMonetaryTotal>
          <InvoiceLine>
            <ID>1</ID><Note>(GTIN)08699578593612(BN)ABC(SN)SERIAL1(XD)281231</Note>
            <InvoicedQuantity unitCode="C62">11</InvoicedQuantity>
            <LineExtensionAmount currencyID="TRY">787.06</LineExtensionAmount>
            <AllowanceCharge><ChargeIndicator>false</ChargeIndicator><Amount>1.00</Amount></AllowanceCharge>
            <AllowanceCharge><ChargeIndicator>false</ChargeIndicator><Amount>2.00</Amount></AllowanceCharge>
            <TaxTotal><TaxSubtotal><TaxAmount>7.87</TaxAmount><Percent>1</Percent></TaxSubtotal></TaxTotal>
            <Item>
              <Description>TEST İLAÇ</Description><Name>TEST İLAÇ</Name>
              <StandardItemIdentification><ID>8699578593612</ID></StandardItemIdentification>
            </Item>
            <Price><PriceAmount>71.55</PriceAmount></Price>
          </InvoiceLine>
        </Invoice>
        """;

    var document = UblInvoiceParser.Parse(xml, "incoming-invoice");

    Assert(document.Ettn == "11111111-2222-3333-4444-555555555555"
           && document.InvoiceFrom.Name == "Test Ecza Deposu"
           && document.InvoiceFrom.Vkn == "1234567890"
           && document.InvoiceTo.Name == "Fikret Acar"
           && document.InvoiceTo.Tckn == "12345678901",
        "compact invoice identifier and linked party identity fields are retained");
    Assert(document.IssueDate == new DateOnly(2026, 8, 25)
           && document.DueDate == new DateOnly(2026, 11, 23),
        "issue and payment due dates are retained");
    Assert(document.LineExtensionAmount == 787.06m && document.TaxAmount == 7.87m
           && document.PayableAmount == 794.93m,
        "requested invoice totals retain their exact decimal scale");
    Assert(document.Lines.Count == 1 && document.Lines[0].DiscountAmount == 3m
           && document.Lines[0].Quantity == 11m && document.Lines[0].UnitPrice == 71.55m,
        "complete structured content and all line discounts are retained");
    Assert(document.Metadata.GetValueOrDefault("DocumentAllowanceAmount") == "4.00",
        "document-level discount is retained separately from line discounts");
    Assert(InvoiceMedicationClassifier.HasItsTraceabilityData(document.Lines[0].Values.Values),
        "UBL content preserves definitive İTS medicine traceability");
}

static void UblSelcukInvoiceContentIsParsed()
{
    const string xml = """
        <Invoice xmlns="urn:oasis:names:specification:ubl:schema:xsd:Invoice-2"
                 xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2"
                 xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2">
          <cbc:ID>H8K2026000770066</cbc:ID>
          <cbc:UUID>596538dd-e320-4bd3-9f5a-bcd07333d52a</cbc:UUID>
          <cbc:IssueDate>2026-08-27</cbc:IssueDate>
          <cbc:DocumentCurrencyCode>TRY</cbc:DocumentCurrencyCode>
          <cac:AccountingSupplierParty><cac:Party>
            <cac:PartyName><cbc:Name>Selçuk Ecza Dep. Tic. ve San. A.Ş.</cbc:Name></cac:PartyName>
          </cac:Party></cac:AccountingSupplierParty>
          <cac:AccountingCustomerParty><cac:Party>
            <cac:PartyName><cbc:Name>Test Eczanesi</cbc:Name></cac:PartyName>
          </cac:Party></cac:AccountingCustomerParty>
          <cac:TaxTotal><cbc:TaxAmount currencyID="TRY">157.89</cbc:TaxAmount></cac:TaxTotal>
          <cac:LegalMonetaryTotal>
            <cbc:LineExtensionAmount currencyID="TRY">1578.90</cbc:LineExtensionAmount>
            <cbc:PayableAmount currencyID="TRY">1736.79</cbc:PayableAmount>
          </cac:LegalMonetaryTotal>
          <cac:InvoiceLine>
            <cbc:ID>1</cbc:ID>
            <cbc:Note>NOT: (A1) ÖDEME TARİHİ : 09.09.2026</cbc:Note>
            <cbc:InvoicedQuantity unitCode="C62">5.00</cbc:InvoicedQuantity>
            <cbc:LineExtensionAmount currencyID="TRY">1578.90</cbc:LineExtensionAmount>
            <cac:TaxTotal><cac:TaxSubtotal>
              <cbc:TaxAmount currencyID="TRY">157.89</cbc:TaxAmount><cbc:Percent>10.00</cbc:Percent>
            </cac:TaxSubtotal></cac:TaxTotal>
            <cac:Item>
              <cbc:Description>MYSOLINE 250 MG 30 TB.</cbc:Description>
              <cbc:Name>MYSOLINE 250 MG 30 TB.</cbc:Name>
              <cac:BuyersItemIdentification><cbc:ID>8680395500020</cbc:ID></cac:BuyersItemIdentification>
              <cac:SellersItemIdentification><cbc:ID>175</cbc:ID></cac:SellersItemIdentification>
              <cac:AdditionalItemIdentification>
                <cbc:ID schemeID="ILAC">(GTIN)8680395500020(BN)26E985(SN)300114001458987(XD)310430</cbc:ID>
              </cac:AdditionalItemIdentification>
            </cac:Item>
            <cac:Price><cbc:PriceAmount currencyID="TRY">315.78</cbc:PriceAmount></cac:Price>
          </cac:InvoiceLine>
        </Invoice>
        """;

    var document = UblInvoiceParser.Parse(xml, "incoming-invoice");

    Assert(document.Lines.Count == 1, "SELÇUK XML content line is retained");
    var line = document.Lines[0];
    Assert(line.Name == "MYSOLINE 250 MG 30 TB." && line.Description == string.Empty,
        "SELÇUK medicine name is retained without duplicate description");
    Assert(line.ItemCode == "175" && line.Barcode == "8680395500020",
        "SELÇUK seller code and GTIN are mapped from their UBL identification nodes");
    Assert(line.Quantity == 5m && line.UnitPrice == 315.78m && line.LineAmount == 1578.90m,
        "SELÇUK quantity and decimal prices retain their scale");
    Assert(document.DueDate == new DateOnly(2026, 9, 9),
        "SELÇUK due date is recovered from the invoice-line note");
    Assert(InvoiceMedicationClassifier.HasItsTraceabilityData(line.Values.Values),
        "SELÇUK İTS identifiers remain available for medicine classification");
}

static void UblUniversalItemIdentifiersPreferInvoiceBarcode()
{
    const string xml = """
        <Invoice>
          <ID>UNIVERSAL-1</ID><UUID>aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee</UUID>
          <IssueDate>2026-08-29</IssueDate><DocumentCurrencyCode>TRY</DocumentCurrencyCode>
          <AccountingSupplierParty><Party><PartyName><Name>Yeni Depo</Name></PartyName></Party></AccountingSupplierParty>
          <AccountingCustomerParty><Party><PartyName><Name>Test Eczanesi</Name></PartyName></Party></AccountingCustomerParty>
          <LegalMonetaryTotal><PayableAmount>100</PayableAmount></LegalMonetaryTotal>
          <InvoiceLine>
            <ID>1</ID><InvoicedQuantity>1</InvoicedQuantity><LineExtensionAmount>100</LineExtensionAmount>
            <Item>
              <Name>Test Barkodlu Ürün</Name>
              <SellersItemIdentification><ID>ÜRÜN STOKTA YOK</ID></SellersItemIdentification>
              <StandardItemIdentification><ID>DEPO-IC-KODU</ID></StandardItemIdentification>
              <AdditionalItemIdentification><ID schemeID="BARKOD">8470003935876</ID></AdditionalItemIdentification>
            </Item>
            <Price><PriceAmount>100</PriceAmount></Price>
          </InvoiceLine>
        </Invoice>
        """;

    var line = UblInvoiceParser.Parse(xml, "incoming-invoice").Lines.Single();
    Assert(line.ItemCode == "ÜRÜN STOKTA YOK" && line.Barcode == "8470003935876",
        "an invoice-declared valid barcode wins over nonnumeric depot stock and availability labels");
}

static void InvoiceLegacyMergeUsesStrongestHeader()
{
    var sender = new LegacyInvoiceMergeParty("Selçuk Ecza Deposu", "", "8699810000014");
    var receiver = new LegacyInvoiceMergeParty("Test Eczanesi", "12345678901", "");
    var weak = MergeCandidate(10, true, sender, receiver, new DateOnly(2026, 1, 10), 0m, 0m, 0m);
    var strong = MergeCandidate(11, true, sender, receiver, new DateOnly(2026, 2, 10), 100m, 5m, 10m);

    var selected = LegacyInvoiceMergeMatcher.Select(
        [weak, strong], sender, receiver, new DateOnly(2026, 2, 10), 100m, 5m, 10m);

    Assert(selected?.SourceFaturaId == 11,
        "the legacy invoice with the strongest financial and due-date evidence is selected");
}

static void InvoiceLegacyMergeConsumesEquivalentSavedDuplicates()
{
    var sender = new LegacyInvoiceMergeParty("Bursa Eczacılar Koop", "", "8680324000010");
    var receiver = new LegacyInvoiceMergeParty("Test Eczanesi", "12345678901", "");
    var later = MergeCandidate(200, true, sender, receiver, null, 0m, 0m, 0m);
    var earlier = MergeCandidate(100, true, sender, receiver, null, 0m, 0m, 0m);

    var selected = LegacyInvoiceMergeMatcher.Select(
        [later, earlier], sender, receiver, null, 500m, 25m, 50m);

    Assert(selected?.SourceFaturaId == 100 && selected.IsSaved,
        "equivalent duplicates with identical saved state are consumed one at a time in stable source order");
}

static void InvoiceLegacyMergeRejectsMixedSavedState()
{
    var sender = new LegacyInvoiceMergeParty("Ecza Deposu", "1234567890", "");
    var receiver = new LegacyInvoiceMergeParty("Test Eczanesi", "12345678901", "");
    var saved = MergeCandidate(300, true, sender, receiver, null, 0m, 0m, 0m);
    var unsaved = MergeCandidate(301, false, sender, receiver, null, 0m, 0m, 0m);

    var selected = LegacyInvoiceMergeMatcher.Select(
        [saved, unsaved], sender, receiver, null, 500m, 25m, 50m);

    Assert(selected is null,
        "a tied legacy header with conflicting saved state remains unmerged instead of guessing");
}

static LegacyInvoiceMergeCandidate MergeCandidate(
    int sourceId,
    bool isSaved,
    LegacyInvoiceMergeParty sender,
    LegacyInvoiceMergeParty receiver,
    DateOnly? dueDate,
    decimal? totalCost,
    decimal? discount,
    decimal? kdv) => new(
        sourceId, $"farmassist:fatura:{sourceId}", isSaved, DateTimeOffset.UtcNow,
        dueDate, totalCost, discount, kdv, sender, receiver);

static void MedulaPageClassifierUsesRenderedLoginFormInsteadOfLoginJspUrl()
{
    Assert(
        MedulaPageClassifier.Classify(
            "https://medeczane.sgk.gov.tr/eczane/faces/login.jsp",
            hasLoginForm: false,
            "Medula Eczane Ana Sayfa") == MedulaPageKind.Authenticated,
        "authenticated home may retain login.jsp URL");
    Assert(
        MedulaPageClassifier.Classify(
            "https://medeczane.sgk.gov.tr/eczane/PostLogoutRedirectServlet",
            hasLoginForm: true,
            "Kullanıcı Girişi") == MedulaPageKind.Login,
        "rendered login form controls unauthenticated classification");
    Assert(
        MedulaPageClassifier.Classify(
            "https://medeczane.sgk.gov.tr/eczane/loginerror.jsp",
            hasLoginForm: false,
            "Kullanıcı adı veya şifre hatalı") == MedulaPageKind.LoginRejected,
        "explicit login rejection remains a failure");
    Assert(
        MedulaPageClassifier.Classify(
            "chrome-error://chromewebdata/",
            hasLoginForm: false,
            "This site can't be reached") == MedulaPageKind.Unexpected,
        "browser error pages are never authenticated");
}

static void MedulaPrescriptionFieldMapMatchesLiveControls()
{
    var input = new MedulaPrescriptionInput(
        "11111111111", "22222222222", "Y", "5", "P-42", "12345678", "18/08/2026", "18/08/2026",
        "2", "6", "DIP-7", "2", "DR-9", "Ada", "Yılmaz", "109", true, true, "A01", true,
        [new MedulaDrugEntry { Selected = true, Barcode = "8690000000001", Quantity = "2", Period = "1", PeriodUnit = "4", DoseCount = "3", DoseAmount = "0,5" }],
        [new MedulaDiagnosisEntry { Code = "A01", Description = "Test tanı" }]);
    var payload = MedulaPrescriptionFieldMapper.Build(input);

    Assert(payload.Values["f:t18"] == "11111111111" && payload.Values["f:text37"] == "22222222222", "patient and recipient ids map to the inspected controls");
    Assert(payload.Values["f:menu2"] == "5" && payload.Values["f:m8"] == "6", "select values retain Medula option codes");
    Assert(payload.Values["f:tbl1:0:t1"] == "8690000000001" && payload.Values["f:tbl1:0:t5"] == "1", "drug row maps barcode and period");
    Assert(payload.Values["f:tableEx1:0:text7"] == "A01" && payload.Values["f:tableEx1:0:text3"] == "Test tanı", "diagnosis row maps code and description");
    Assert(payload.Checks["f:checkbox1"] && payload.Checks["f:tbl1:0:checkbox7"] && payload.Checks["f:checkbox4"], "page and row checkboxes map to live ids");
}

static void MedulaDatesAlwaysUseFourDigitYears()
{
    Assert(MedulaDateNormalizer.Normalize("12/08/26") == "12/08/2026"
        && MedulaDateNormalizer.Normalize("12.8.2026") == "12/08/2026"
        && MedulaDateNormalizer.Normalize(string.Empty, new DateTime(2026, 8, 12)) == "12/08/2026",
        "two-digit and selected dates are normalized to Medula's dd/MM/yyyy payload");
}

static void MedulaBrowserWarningsRemainExactAndPreferAlerts()
{
    const string exactAlert = "  Reçete Ekranına yeniden giriş yapınız\nİkinci satır  ";
    Assert(
        MedulaBrowserWarning.First([exactAlert], ["Onay metni"]) == exactAlert,
        "Medula alert text is surfaced one-to-one without rewriting");
    Assert(
        MedulaBrowserWarning.First(["", "  "], ["Medula onayı"]) == "Medula onayı",
        "confirmation text is the universal fallback when no alert exists");
    Assert(
        MedulaBrowserWarning.First([], []) is null,
        "the universal handler stays silent when Medula emitted no warning");
}

static void MedulaWarningBridgeNeverReopensNativeDialogs()
{
    Assert(MedulaWarningBridge.SuppressNativeScriptDialogs,
        "WebView2 native script dialogs are disabled so they cannot block WPF rendering");
    Assert(MedulaWarningBridge.Script.Contains("window.alert = message =>", StringComparison.Ordinal)
        && !MedulaWarningBridge.Script.Contains("originalAlert", StringComparison.Ordinal)
        && !MedulaWarningBridge.Script.Contains("alert.apply", StringComparison.Ordinal),
        "the document bridge forwards alerts without reopening the browser modal");
    Assert(MedulaWarningBridge.Script.Contains("window.top.postMessage", StringComparison.Ordinal),
        "warnings raised in child frames are relayed through the top-level WebView document");
    Assert(MedulaWarningBridge.Script.Contains("[cç]al[ıi][sş]", StringComparison.Ordinal),
        "doctor and facility employment mismatches are classified as validation errors");
    Assert(MedulaWarningBridge.Script.Contains("[role=\"status\"]", StringComparison.Ordinal)
        && MedulaWarningBridge.Script.Contains("semantic || colour", StringComparison.Ordinal),
        "semantic and colour-coded Medula messages are observed without requiring known wording");
    Assert(MedulaWarningBridge.Script.Contains("d[uü]zenlendi", StringComparison.Ordinal)
        && MedulaWarningBridge.Script.Contains("maksimum\\s+ila[cç]\\s+adet", StringComparison.Ordinal),
        "SUT maximum-quantity adjustment notices are classified and relayed");
}

static void MedulaSaveGuardRequiresDefinitiveRemoteState()
{
    var summary = new MedulaSavedPrescriptionSummary(
        "Reçete Tutarları",
        [new MedulaPrescriptionPaymentField("Toplam Tutar", "1,00")],
        ["Kaydet"]);
    MedulaPrescriptionPageResult Result(
        string prescriptionNumber,
        MedulaSavedPrescriptionSummary? savedSummary,
        string? warning = null) => new(
            true,
            true,
            "Reçete",
            string.Empty,
            string.Empty,
            string.Empty,
            FieldValues: new Dictionary<string, string>
            {
                ["medula:prescription-no"] = prescriptionNumber
            },
            WarningText: warning,
            SavedSummary: savedSummary);

    Assert(!MedulaPrescriptionSaveGuard.IsConfirmed(Result("0", summary)),
        "a successful-looking draft page with no assigned Medula number cannot be persisted");
    Assert(!MedulaPrescriptionSaveGuard.IsConfirmed(Result("12345", null)),
        "an assigned-looking number without the saved payment screen cannot be persisted");
    Assert(!MedulaPrescriptionSaveGuard.IsConfirmed(Result("12345", summary, "Doktor bu tesiste çalışmamaktadır")),
        "any Medula validation warning blocks local persistence even when saved fields remain visible");
    Assert(MedulaPrescriptionSaveGuard.IsConfirmed(Result("12345", summary)),
        "payment fields plus an assigned Medula number constitute the definitive saved state");
}

static void MedulaDoctorSpecialtyRespectsDefaultRules()
{
    Assert(
        MedulaDoctorReconciliation.SpecialtyForPersistence("Pratisyen Hekim", false, false) is null,
        "the untouched default specialty does not overwrite an existing doctor");
    Assert(
        MedulaDoctorReconciliation.SpecialtyForPersistence("Pratisyen Hekim", true, false)
            == "Pratisyen Hekim"
        && MedulaDoctorReconciliation.SpecialtyForPersistence("Pratisyen Hekim", false, true)
            == "Pratisyen Hekim",
        "an explicit selection or completed prescription may persist the default specialty");
    Assert(
        MedulaDoctorReconciliation.SpecialtyForPersistence("Kardiyoloji", false, false) == "Kardiyoloji",
        "a non-default Medula specialty is always authoritative");
}

static void MedulaDoctorImportUsesNormalizedBranchRelation()
{
    Assert(MedulaDirectorySql.InsertDoctorIfMissing.Contains("branch_id", StringComparison.Ordinal)
        && !MedulaDirectorySql.InsertDoctorIfMissing.Contains("specialty", StringComparison.OrdinalIgnoreCase)
        && !MedulaDirectorySql.InsertDoctorIfMissing.Contains("certificate", StringComparison.OrdinalIgnoreCase),
        "automatic Medula doctor insertion targets the normalized branches relation without prescription-owned certificate data");
}

static void MedulaPrescriptionListRefreshHasExplicitTriggers()
{
    Assert(MedulaPrescriptionListRefreshPolicy.DefaultQuickRangeDays == 1,
        "the prescription list opens with the 1 Gün quick range");
    Assert(MedulaPrescriptionListRefreshPolicy.RequiresReachabilityRefresh(
            MedulaPrescriptionListRefreshCause.InitialOpen)
        && MedulaPrescriptionListRefreshPolicy.RequiresReachabilityRefresh(
            MedulaPrescriptionListRefreshCause.ExplicitRefresh)
        && MedulaPrescriptionListRefreshPolicy.RequiresReachabilityRefresh(
            MedulaPrescriptionListRefreshCause.FilterChange),
        "initial load, explicit refresh, and remote filter changes request the reachability update");
    Assert(!MedulaPrescriptionListRefreshPolicy.RequiresReachabilityRefresh(
            MedulaPrescriptionListRefreshCause.LocalViewInteraction),
        "table row interaction never requests the reachability update");
    Assert(!MedulaPrescriptionListRefreshPolicy.ShouldStopAfterEmptyPeriod(1)
        && MedulaPrescriptionListRefreshPolicy.ShouldStopAfterEmptyPeriod(2),
        "reachability scanning stops only after two consecutive empty periods");
}

static void MedulaSubScreenActionsAreStrictlyAllowlisted()
{
    Assert(MedulaSubScreenSafety.IsAllowed("drugs", "Göz İlaçları"), "eye-drug list is available");
    Assert(!MedulaSubScreenSafety.IsAllowed("eye-drugs", "Göz İlaçları"),
        "the redundant eye-drug action is not exposed after the backend eye screen opens");
    Assert(MedulaSubScreenSafety.IsAllowed("reports", "Bitmiş raporları da göster"), "finished reports are available");
    Assert(MedulaSubScreenSafety.IsAllowed("reports", "Yeni Kayıt"), "new report draft is available");
    Assert(MedulaSubScreenSafety.IsAllowed("report-new", "Ara")
        && MedulaSubScreenSafety.IsAllowed("report-new", "Ekle")
        && MedulaSubScreenSafety.IsAllowed("report-new", "Sorgula"),
        "requested report draft controls are available");
    Assert(MedulaSubScreenSafety.IsAllowed("report-selection", "Tamam")
        && !MedulaSubScreenSafety.IsAllowed("report-selection", "Vazgeç"),
        "report choices can be accepted while cancellation remains bound to the explicit popup close control");
    Assert(MedulaSubScreenSafety.IsAllowed("treatment-scheme", "Tamam")
        && !MedulaSubScreenSafety.IsAllowed("treatment-scheme", "Geri Dön"),
        "treatment values can be accepted while closing remains bound to the popup close control");
    Assert(MedulaSubScreenSafety.IsAllowed("new-warning-code", "Tamam"),
        "the new warning-code table accepts its explicit confirmation action");
    Assert(MedulaSubScreenSafety.ShouldReturnToPrescriptionAfterAccept(
            "treatment-scheme", "Tamam")
        && !MedulaSubScreenSafety.ShouldReturnToPrescriptionAfterAccept(
            "new-warning-code", "Tamam"),
        "only treatment scheme confirmation requires the following Medula return action");
    Assert(!MedulaSubScreenSafety.ShouldSynchronizePrescriptionInput("f:buttonRaporSecim")
        && !MedulaSubScreenSafety.ShouldSynchronizePrescriptionInput("f:buttonRaporGoruntule")
        && !MedulaSubScreenSafety.ShouldSynchronizePrescriptionInput("f:buttonIlacTedaviSema")
        && !MedulaSubScreenSafety.ShouldSynchronizePrescriptionInput("f:buttonReceteTeshis")
        && MedulaSubScreenSafety.ShouldSynchronizePrescriptionInput("f:buttonIlacBilgiGorme"),
        "report-dependent actions preserve Medula's live unsaved e-prescription state while ordinary actions still synchronize input");
    Assert(!MedulaSubScreenSafety.IsAllowed("off-label-old", "Güncelle"), "off-label update is blocked");
    Assert(!MedulaSubScreenSafety.IsAllowed("reports", "Kaydet"), "report saving is blocked");
    Assert(!MedulaSubScreenSafety.IsAllowed("drugs", "Sil"), "drug deletion is blocked");
    Assert(MedulaSubScreenSafety.IsAllowed("its-qr", "Listele")
        && MedulaSubScreenSafety.IsAllowed("its-qr", "ITS Satış Bildirimi")
        && MedulaSubScreenSafety.IsAllowed("its-qr", "ITS Satış İptali"),
        "ITS QR validation and the explicitly authorized sale/cancellation notifications are available");
    Assert(MedulaSubScreenSafety.IsAllowed(
            "uts-operations", "Yeni Kayıt Ekle/Seçilen Kaydı Güncelle")
        && MedulaSubScreenSafety.IsAllowed("uts-operations", "Yeni Kayıt")
        && !MedulaSubScreenSafety.IsAllowed("uts-operations", "Sil"),
        "ÜTS exposes only its observed add/update and form-reset actions");
    Assert(MedulaSubScreenSafety.IsSafePageCaption("2")
        && MedulaSubScreenSafety.IsSafePageCaption("İleri")
        && !MedulaSubScreenSafety.IsSafePageCaption("Kaydet"),
        "only paging captions can drive generic table pagination");
    Assert(MedulaSubScreenSafety.IsEnteringItsQrScreen("its-qr", "its-qr", false),
        "a hidden stale ITS tag does not suppress automatic Listele on reopen");
    Assert(!MedulaSubScreenSafety.IsEnteringItsQrScreen("its-qr", "its-qr", true),
        "an ITS refresh does not recursively trigger Listele again");
}

static void MedulaEditablePopupLayoutGroupsDoseFactors()
{
    var rows = new[]
    {
        new MedulaTreatmentSchemeRow(
            "XOLAIR 150 MG/ML",
            [
                new MedulaTreatmentSchemeCell("period", "Periyot:", "1", "text", false, []),
                new MedulaTreatmentSchemeCell("dose", "Doz", "2", "text", false, []),
                new MedulaTreatmentSchemeCell("factor", "x", "3", "text", false, [])
            ])
    };

    var groups = MedulaEditableTableLayout.BuildColumnGroups(rows);
    Assert(groups.Count == 2
        && groups[0].Label == "Periyot"
        && groups[0].CellIndexes.SequenceEqual([0])
        && groups[1].Label == "Doz"
        && groups[1].CellIndexes.SequenceEqual([1, 2]),
        "editable Medula popups expose one Doz header while preserving two distinct dose controls");
    Assert(rows[0].MedicationName == "XOLAIR 150 MG/ML"
        && rows[0].Cells[1].FieldId == "dose"
        && rows[0].Cells[2].FieldId == "factor",
        "the medication name and both editable dose field identities remain intact");
}

static void MedulaSubScreensRequireBackendDestinationProof()
{
    Assert(!MedulaSubScreenSafety.IsExpectedDestination(
        "drugs",
        "https://medeczane.sgk.gov.tr/eczane/pages/recete/ReceteGiris.jsp",
        ["f:buttonIlacListesi", "f:buttonReceteKaydet"]),
        "a click that leaves Medula on Reçete cannot open the native drug screen");
    Assert(MedulaSubScreenSafety.IsExpectedDestination(
        "eye-drugs",
        "https://medeczane.sgk.gov.tr/eczane/pages/haksahibi/IlacListe.jsp;jsessionid=test-session",
        ["form1:tableExKisiIlacList"]),
        "the eye-drug screen requires its real owner-history route and data table");
    Assert(!MedulaSubScreenSafety.IsExpectedDestination(
        "eye-drugs",
        "https://medeczane.sgk.gov.tr/eczane/faces/pages/recete/ReceteIslem2.jsp",
        ["form1:tableExKisiIlacList"],
        ["Göz İlaçları"]),
        "the ordinary medicine screen cannot masquerade as Göz İlaçları");
    Assert(MedulaSubScreenSafety.IsExpectedDestination(
        "drugs",
        "https://medeczane.sgk.gov.tr/eczane/faces/pages/recete/ReceteIslem2.jsp",
        ["form1:buttonGeriDon"],
        ["Göz İlaçları"]),
        "the live drug page is accepted from its unique visible action");
    Assert(!MedulaSubScreenSafety.IsExpectedDestination(
        "drugs",
        "https://medeczane.sgk.gov.tr/eczane/faces/pages/recete/ReceteIslem2.jsp",
        ["f:buttonReceteKaydet"],
        ["Göz İlaçları"]),
        "prescription controls prevent a false drug-screen match");
    Assert(MedulaSubScreenSafety.IsExpectedDestination(
        "reports",
        "https://medeczane.sgk.gov.tr/eczane/pages/haksahibi/RaporListe.jsp",
        []),
        "an empty report screen is accepted from its real backend route");
    Assert(MedulaSubScreenSafety.IsExpectedDestination(
        "reports",
        "https://medeczane.sgk.gov.tr/eczane/faces/pages/recete/ReceteIslem2.jsp",
        ["form1:buttonGeriDon"],
        ["Bitmiş Raporları da Göster", "Yeni Kayıt"]),
        "the live same-route report page is accepted from its unique actions");
    Assert(MedulaSubScreenSafety.IsExpectedDestination(
        "examinations",
        "https://medeczane.sgk.gov.tr/eczane/pages/haksahibi/MuayeneDetay.jsp",
        []),
        "an empty examination screen is accepted from its real backend route");
    Assert(MedulaSubScreenSafety.IsExpectedDestination(
        "examinations",
        "https://medeczane.sgk.gov.tr/eczane/faces/pages/recete/ReceteIslem2.jsp",
        ["f:buttonReceteKaydet", "form1:tableEx3"],
        []),
        "the live same-route examination page is accepted from its unique table even when Reçete controls remain in the DOM");
    Assert(MedulaSubScreenSafety.IsExpectedDestination(
        "examinations",
        "https://medeczane.sgk.gov.tr/eczane/faces/pages/recete/ReceteIslem2.jsp",
        ["form1:buttonGeriDon", "form1:tableEx1"],
        ["Geri Dön"]),
        "the alternate live examination list is accepted when Medula omits tableEx3");
    Assert(!MedulaSubScreenSafety.IsExpectedDestination(
        "examinations",
        "https://medeczane.sgk.gov.tr/eczane/faces/pages/recete/ReceteIslem2.jsp",
        ["form1:buttonGeriDon", "form1:buttonGuncelle", "form1:tableEx1"],
        ["Geri Dön"])
        && !MedulaSubScreenSafety.IsExpectedDestination(
            "examinations",
            "https://medeczane.sgk.gov.tr/eczane/faces/pages/recete/ReceteIslem2.jsp",
            ["form1:buttonGeriDon", "form1:tableEx1"],
            ["Bitmiş Raporları da Göster", "Yeni Kayıt"]),
        "the alternate examination proof does not accept off-label or report pages");
    Assert(MedulaSubScreenSafety.IsExpectedDestination(
        "off-label-old",
        "https://medeczane.sgk.gov.tr/eczane/pages/recete/EndikasyonDisiIzinIslem.jsp",
        []),
        "the old off-label screen is accepted from its real backend route");
    Assert(MedulaSubScreenSafety.IsExpectedDestination(
        "off-label-new",
        "https://medeczane.sgk.gov.tr/eczane/pages/recete/KisiEndikasyonDisiIzinIslem.jsp",
        []),
        "the new off-label screen is accepted from its real backend route");
    Assert(MedulaSubScreenSafety.IsExpectedDestination(
        "off-label-old",
        "https://medeczane.sgk.gov.tr/eczane/faces/pages/recete/ReceteIslem2.jsp",
        ["form1:buttonGeriDon", "form1:buttonGuncelle"]),
        "the live same-route off-label response is accepted from its unique controls");
    Assert(!MedulaSubScreenSafety.IsExpectedDestination(
        "off-label-old",
        "https://medeczane.sgk.gov.tr/eczane/faces/pages/recete/ReceteIslem2.jsp",
        ["f:buttonReceteKaydet", "form1:buttonGeriDon", "form1:buttonGuncelle"]),
        "prescription controls prevent a false off-label match");
    Assert(!MedulaSubScreenSafety.IsExpectedDestination(
        "reports",
        "https://medeczane.sgk.gov.tr/eczane/pages/recete/ReceteGiris.jsp",
        ["f:buttonRaporListesi"]),
        "a Medula validation error cannot open the native report screen");
    Assert(MedulaSubScreenSafety.IsExpectedDestination(
        "facility-search",
        "https://medeczane.sgk.gov.tr/eczane/pages/recete/ReceteGiris.jsp",
        ["f:box14", "f:buttonVazgec", "f:buttonTesisSehriAra", "f:menu3", "f:text74"]),
        "inline lookup requires visible panel markers");
    Assert(MedulaSubScreenSafety.IsExpectedDestination(
        "facility-departments",
        "https://medeczane.sgk.gov.tr/eczane/faces/pages/recete/ReceteIslem2.jsp",
        ["f:box4", "f:tableExKonuSorguList", "f:buttonKonuTamam"]),
        "the live facility departments panel is recognized");
    Assert(MedulaSubScreenSafety.IsExpectedDestination(
        "branch-select",
        "https://medeczane.sgk.gov.tr/eczane/faces/pages/recete/ReceteIslem2.jsp",
        ["f:box18", "f:tableExBransList"]),
        "the live branch panel is recognized without a nonexistent cancel control");
    Assert(MedulaSubScreenSafety.IsExpectedDestination(
        "warning-code",
        "https://medeczane.sgk.gov.tr/eczane/faces/pages/recete/ReceteIslem2.jsp",
        ["f:box39", "f:buttonReceteTeshisPanelKapat"]),
        "the live warning-code panel is recognized from its actual close control");
    Assert(MedulaSubScreenSafety.IsExpectedDestination(
        "new-warning-code",
        "https://medeczane.sgk.gov.tr/eczane/faces/pages/recete/ReceteIslem2.jsp",
        ["__new_warning_code_dialog__", "f:newGeneratedWarningControl"],
        ["Vazgeç"]),
        "the generated new warning-code dialog is recognized without depending on the legacy box39 id");
    Assert(!MedulaSubScreenSafety.IsExpectedDestination(
        "new-warning-code",
        "https://medeczane.sgk.gov.tr/eczane/faces/pages/recete/ReceteIslem2.jsp",
        ["f:box39", "f:buttonReceteTeshisPanelKapat"],
        ["Kapat"]),
        "the legacy warning-code panel cannot masquerade as the new warning-code dialog");
    Assert(MedulaSubScreenSafety.IsExpectedDestination(
        "treatment-scheme",
        "https://medeczane.sgk.gov.tr/eczane/faces/pages/recete/IlacTedaviSema.jsp;jsessionid=test",
        ["form1:buttonGeriDon"]),
        "the treatment-scheme destination is accepted from its Medula page and return control");
    Assert(MedulaSubScreenSafety.IsExpectedDestination(
        "report-selection",
        "https://medeczane.sgk.gov.tr/eczane/faces/pages/recete/ReceteIslem2.jsp",
        [],
        ["Tamam", "Vazgeç"]),
        "the report-selection overlay is recognized from its own controls on every prescription page variant");
    Assert(MedulaSubScreenSafety.IsExpectedDestination(
        "drug-report-detail",
        "https://medeczane.sgk.gov.tr/eczane/faces/pages/rapor/RaporGorme.jsp;jsessionid=test",
        ["form1:buttonGeriDon"]),
        "a medication report opens only on the real Medula report-detail destination");
    Assert(MedulaSubScreenSafety.IsExpectedDestination(
        "uts-operations",
        "https://medeczane.sgk.gov.tr/eczane/faces/pages/recete/ReceteIslem2.jsp",
        ["form1:buttonGeriDon", "form1:tableExIlacBilgileri", "form1:buttonGuncelle", "form1:buttonYeniKayit"],
        ["Geri Dön", "Yeni Kayıt"]),
        "the same-route ÜTS screen is recognized from its return control and form content");
    Assert(MedulaSubScreenSafety.InlineRootId("facility-search") == "f:box11"
        && MedulaSubScreenSafety.InlineRootId("facility-departments") == "f:box4"
        && MedulaSubScreenSafety.InlineRootId("branch-select") == "f:box18"
        && MedulaSubScreenSafety.InlineRootId("warning-code") == "f:box39"
        && MedulaSubScreenSafety.InlineRootId("new-warning-code") is null,
        "fixed overlays use their live ids while the generated warning dialog is structurally scoped");
    Assert(MedulaSubScreenSafety.IsAllowedActionId(
            "branch-select", "f:tableExBransList:42:rowAction5")
        && MedulaSubScreenSafety.IsAllowedActionId(
            "facility-search", "f:tableExTesisSorguList:7:rowAction1")
        && MedulaSubScreenSafety.IsAllowedActionId(
            "uts-operations", "form1:tableExIlacBilgileri:1:rowAction1")
        && !MedulaSubScreenSafety.IsAllowedActionId(
            "branch-select", "f:tableExBransList:42:buttonGuncelle")
        && !MedulaSubScreenSafety.IsAllowedActionId(
            "facility-search", "f:tableExTesisSorguList:7:buttonGuncelle")
        && !MedulaSubScreenSafety.IsAllowedActionId(
            "reports", "f:tableExBransList:42:rowAction5"),
        "only allowlisted branch, facility, and ÜTS record selections can be forwarded by double-click");
    Assert(MedulaSubScreenSafety.RequiresPrescriptionReload(
            "Reçete Ekranına yeniden giriş yapınız")
        && MedulaSubScreenSafety.RequiresPrescriptionReload(
            "Reçete Giriş ekranına yeniden giriş yapınız.")
        && !MedulaSubScreenSafety.RequiresPrescriptionReload("Tesis kodu zorunludur."),
        "only Medula's explicit stale-prescription response triggers automatic reload");
    Assert(
        MedulaSubScreenSafety.ShouldRetryAfterPrescriptionReload(
            "Reçete Giriş ekranına yeniden giriş yapınız.", 0)
        && !MedulaSubScreenSafety.ShouldRetryAfterPrescriptionReload(
            "Reçete Giriş ekranına yeniden giriş yapınız.", 1),
        "a stale prescription retries the requested destination exactly once");
}

static void MedulaSubScreenFailureWaitIsBounded()
{
    Assert(MedulaSubScreenSafety.MaximumDestinationWait < TimeSpan.FromSeconds(3),
        "sub-screen failure detection no longer reaches the generic 30-second navigation timeout");
    Assert(MedulaSubScreenSafety.DestinationPollIntervalMilliseconds <= 100,
        "successful destination changes are detected promptly");
}

static void MedulaRemoteScreensAreRefined()
{
    Assert(MedulaRemoteScreenRefiner.ResolveVerifiedIdentity("36928637****", "36928637868") == "36928637868"
        && MedulaRemoteScreenRefiner.ResolveVerifiedIdentity("36928637868", "11111111111") == "36928637868",
        "masked Medula identities fall back to the entered T.C. while an unmasked Medula identity stays authoritative");

    var fields = MedulaRemoteScreenRefiner.RefineFields("warning-code",
    [
        new MedulaRemoteField("f:menu10", "f:menu10", "1", "select", false, []),
        new MedulaRemoteField("f:menu5", "Uyarı Kodları", "1", "select", false, [])
    ]);
    Assert(fields[0].Label == "İlgili Uyarı Kodu" && fields[1].Label == "Uyarı Kodları",
        "warning-code fields have useful Turkish labels");

    var tables = MedulaRemoteScreenRefiner.RefineTables("branch-select",
    [
        new MedulaRemoteTable("f:tableExBransListhead2", ["Branş", "Alan 2"], [["Branş"]]),
        new MedulaRemoteTable(
            "f:tableExBransList",
            ["Alan 1", "Alan 2"],
            [["Pratisyen Hekim", ""], ["|--> Aile Hekimliği", ""]],
            ["f:tableExBransList:0:rowAction5", "f:tableExBransList:1:rowAction5"])
    ]);
    Assert(tables.Count == 1 && tables[0].Headers.SequenceEqual(["Ana Branş", "Alt Branş"])
        && tables[0].Rows[0].SequenceEqual(["Pratisyen Hekim", ""])
        && tables[0].Rows[1].SequenceEqual(["", "└──›  Aile Hekimliği"])
        && tables[0].RowParentValues?[1] == "Pratisyen Hekim"
        && tables[0].RowActionIds?[1] == "f:tableExBransList:1:rowAction5",
        "branch screen draws children below their parent without repeating the parent name");

    var drugTables = MedulaRemoteScreenRefiner.RefineTables("drugs",
    [
        new MedulaRemoteTable(
            "form1:tableExKisiIlacList",
            ["Reçete No", "İlaç Adı", "Reçete Tar.", "İlaç Alım Tar.", "Verilebileceği Tarih", "Adet", "İlaç Kullanımı", "Rap. Teşhisi/Rap. Tak. No", "UK"],
            [
                ["", "R-1", "Test İlaç", "18/08/2026", "19/08/2026", "20/08/2026", "1", "Günde 1 x 1.0", "04.03 - 123", ""],
                ["1 2"]
            ],
            ["form1:tableExKisiIlacList:0:rowAction2"])
    ]);
    Assert(drugTables.Count == 1
        && drugTables[0].Headers.SequenceEqual([
            "Reçete No", "İlaç Adı", "Reçete Tarihi", "İlaç Alım Tarihi", "Verilebileceği Tarih",
            "Adet", "İlaç Kullanımı", "Rap. Teşhisi / Rap. Tak. No", "UK"])
        && drugTables[0].Rows[0].SequenceEqual([
            "R-1", "Test İlaç", "18/08/2026", "19/08/2026", "20/08/2026",
            "1", "Günde 1 x 1.0", "04.03 - 123", ""])
        && drugTables[0].Rows.Count == 1
        && drugTables[0].RowActionIds is null,
        "drug table removes hidden action and pager rows without shifting visible dates and report data");

    var itsTables = MedulaRemoteScreenRefiner.RefineTables("its-qr",
    [
        new MedulaRemoteTable(
            "form1:tableEx1",
            ["", "Barkod", "Adı", "Reçete Adet", "Karekod Adet"],
            [["", "8699569090717", "GLIFOR 1000 MG", "1", "1"]],
            ["unsafe-row-action"])
    ]);
    Assert(itsTables.Count == 1
        && itsTables[0].Headers.SequenceEqual(["Barkod", "Adı", "Reçete Adet", "Karekod Adet"])
        && itsTables[0].Rows[0].SequenceEqual(["8699569090717", "GLIFOR 1000 MG", "1", "1"])
        && itsTables[0].RowActionIds is null,
        "ITS QR medication rows mirror Medula while excluding row actions");
    Assert(MedulaRemoteScreenRefiner.ItsQrQuantitiesMatch("1", "1")
        && MedulaRemoteScreenRefiner.ItsQrQuantitiesMatch("1,5", "1,5")
        && !MedulaRemoteScreenRefiner.ItsQrQuantitiesMatch("2", "1")
        && !MedulaRemoteScreenRefiner.ItsQrQuantitiesMatch("0", "0"),
        "ITS QR rows are green only when a positive prescribed quantity exactly matches the listed QR quantity");
    Assert(MedulaRemoteScreenRefiner.ResolveItsQrCodes("", "QR-A\nQR-B").SequenceEqual(["QR-A", "QR-B"])
        && MedulaRemoteScreenRefiner.ResolveItsQrCodes("QR-LIVE", "QR-LOCAL").SequenceEqual(["QR-LIVE"]),
        "ITS QR codes fall back to the local prescription only when Medula no longer retains them");

    var utsTables = MedulaRemoteScreenRefiner.RefineTables("uts-operations",
    [
        new MedulaRemoteTable(
            "form1:tableExIlacBilgileri",
            ["Alan 1", "Barkod", "İlaç Adı"],
            [["", "8698694560027", "GOLDFINE PLUS"]],
            ["form1:tableExIlacBilgileri:0:rowAction1"])
    ]);
    Assert(utsTables.Count == 1
        && utsTables[0].Title == "ÜTS Kapsamındaki İlaçlar"
        && utsTables[0].Headers.SequenceEqual(["Seç", "Barkod", "İlaç Adı"])
        && utsTables[0].RowActionIds?[0] == "form1:tableExIlacBilgileri:0:rowAction1",
        "ÜTS keeps its safe record-selection action and presents named device columns");

    var reportFields = MedulaRemoteScreenRefiner.RefineFields("report-new",
    [
        new MedulaRemoteField("form1:text28", ":", "", "text", false, []),
        new MedulaRemoteField("form1:text46", "Doktor'un üniversite açıklaması", "", "text", false, []),
        new MedulaRemoteField("form1:tableEx5:2:text83", "form1:tableEx5:2:text83", "", "text", false, [])
    ]);
    Assert(reportFields[0].Label == "Rapor Numarası (*)"
        && reportFields[1].Label == "Dr. Diploma No"
        && reportFields[2].Label == "ICD-10 Kodu 3",
        "new-report controls receive stable labels instead of punctuation or tooltip text");

    var detailTables = MedulaRemoteScreenRefiner.RefineTables("report-detail",
    [
        new MedulaRemoteTable("form1:tableExRaporDoktorList", [],
            [["", "", "13975", "119072", "Aile Hekimliği Uzmanı", "HAKAN", "İÇME"]]),
        new MedulaRemoteTable("form1:tableExRaporTeshisList", [],
            [["", "", "Z93.1", "GASTROSTOMİ DURUMU", "09/06/2026", "09/12/2026"]]),
        new MedulaRemoteTable("form1:tableEx1", [],
            [["", "", "SGK-123", "ETKİN MADDE", "Tablet", "1 x 1", "1", "10 mg", "06/09/2026", ""]])
    ]);
    Assert(detailTables.Count == 3
        && detailTables[0].Title == "Tanı Bilgileri"
        && detailTables[0].Rows[0].Take(2).SequenceEqual(["Z93.1", "GASTROSTOMİ DURUMU"])
        && detailTables[1].Title == "Doktor Bilgileri"
        && detailTables[1].Headers.SequenceEqual(["Dr. Diploma No", "Dip. Tescil No", "Branş", "Adı", "Soyadı"])
        && detailTables[1].Rows[0].Take(2).SequenceEqual(["13975", "119072"])
        && detailTables[2].Headers[0] == "SGK Kodu"
        && detailTables[2].Rows[0][0] == "SGK-123",
        "report detail aligns diagnosis, doctor, and SGK values after hidden Medula cells");

    var facilityTables = MedulaRemoteScreenRefiner.RefineTables("facility-search",
    [
        new MedulaRemoteTable(
            "f:tableExTesisSorguList",
            ["Alan 1", "Sıra No", "Tesis Kodu", "Tesis Adı"],
            [["", "1", "12345678", "Test Hastanesi"]],
            ["f:tableExTesisSorguList:0:rowAction1"])
    ]);
    Assert(facilityTables.Count == 1
        && facilityTables[0].Headers.SequenceEqual(["Sıra No", "Tesis Kodu", "Tesis Adı"])
        && facilityTables[0].Rows[0].SequenceEqual(["1", "12345678", "Test Hastanesi"])
        && facilityTables[0].RowActionIds?[0] == "f:tableExTesisSorguList:0:rowAction1",
        "facility results remain aligned and retain their safe selection action");
    Assert(MedulaRemoteScreenRefiner.FindColumnValue(
            facilityTables[0].Headers,
            facilityTables[0].Rows[0],
            "Tesis Kodu") == "12345678",
        "facility selection copies the code column rather than the row number or facility name");
}

static void PopulatedPayloadRetainsKnownAndUnknownFields()
{
    const string summary = """{"as_of_date":"2026-08-13","sections":{"Ayaktan Raporsuz":{"is_reimbursed":true,"details":[{"sgkem":"4.2.1","gelecek_alan":"korundu","odenme_durumu":[{"odenmeDurumu":"Ödenir"}]}]}}}""";
    const string info = """[{"tek_doz_miktari":"2","tek_doz_birimi":"tablet","medula_tani_kod":"A01$B02","yeni_bilgi":"değer"}]""";
    const string details = """{"ayaktanRaporlu":{"uzmanHekim":[{"receteTani":"Tanı","iliskiliICD10":["A01"]}]}}""";
    var result = SutOverviewFormatter.Parse(summary, info, details);
    Assert(result.DateDisplay.Contains("13.08.2026"), "date");
    Assert(result.Sections.Any(section => section.Header == "Ayaktan Raporsuz" && section.Status == "Ödenir" && section.Content.Contains("Gelecek Alan")), "summary");
    var summarySection = result.Sections.Single(section => section.Header == "Ayaktan Raporsuz");
    Assert(summarySection.HasDetails && summarySection.DetailItems.Count == 1 && summarySection.DetailItems[0].Content.Contains("Gelecek Alan"), "nested summary details");
    Assert(result.Sections[0].Content.Contains("2 tablet") && result.Sections[0].Content.Contains("A01") && result.Sections[0].Content.Contains("Yeni Bilgi"), "medula info");
    Assert(result.Sections[1].Content.Contains("Uzman Hekim") && result.Sections[1].Content.Contains("İlişkili ICD-10"), "medula detail");
}

static void EmptyPayloadIsDistinguished()
{
    var result = SutOverviewFormatter.Parse("{}", "[]", "{}");
    Assert(result.Sections[0].Content == "MEDULA bilgisi bulunamadı.", "empty info");
    Assert(result.Sections[1].Content == "Bilgi bulunamadı.", "empty detail");
    Assert(result.Sections[2].Content == "Bilgi bulunamadı.", "empty summary");
}

static void NullPayloadIsDistinguished()
{
    var result = SutOverviewFormatter.Parse(null, null, null);
    Assert(result.Sections.All(section => section.Content == "Bilgi alınamadı."), "null state");
}

static void UnknownNestedKeysRemainVisible()
{
    var result = SutOverviewFormatter.Parse(null, null, """{"yeniDal":{"bilinmeyen_deger":42}}""");
    Assert(result.Sections[1].Content.Contains("Yeni Dal") && result.Sections[1].Content.Contains("Bilinmeyen Deger") && result.Sections[1].Content.Contains("42"), "unknown fields");
}

static void MalformedPayloadIsReported()
{
    var result = SutOverviewFormatter.Parse("{", "[", "not-json");
    Assert(result.Sections.All(section => section.Content == "Bilgi okunamadı."), "malformed state");
}

static void SutOptionalUiStateIsDerivedFromUsableData()
{
    var populated = SutOverviewFormatter.Parse("""{"sections":{"Yatan":{"is_reimbursed":true,"details":[{"aciklama":"Koşul"}]}}}""", null, null);
    var visible = populated.Sections.Where(section => section.IsTemporarilyVisible).ToArray();
    Assert(visible.Any(section => section.IsUsableSutContent), "SUT tab visible for summary data");
    Assert(visible.Length == 1 && visible[0].Header == "Yatan", "MEDULA sections hidden");

    var empty = SutOverviewFormatter.Parse(null, null, null);
    var emptyVisible = empty.Sections.Where(section => section.IsTemporarilyVisible).ToArray();
    Assert(!emptyVisible.Any(section => section.IsUsableSutContent) && emptyVisible.Length == 1, "SUT tab hidden for placeholder-only data");
}

static void SutHtmlIsConvertedToReadableText()
{
    var result = SutOverviewFormatter.Parse(
        """{"sections":{"Ayaktan Raporsuz":{"is_reimbursed":true,"details":[{"aciklama":"&lt;p&gt;İlk koşul&lt;br&gt;&lt;strong&gt;İkinci koşul&lt;/strong&gt;&lt;/p&gt;"}]}}}""",
        null,
        null);
    var section = result.Sections.Single(item => item.Header == "Ayaktan Raporsuz");
    Assert(section.Content.Contains("İlk koşul") && section.Content.Contains("İkinci koşul"), "HTML text retained");
    Assert(!section.Content.Contains('<') && !section.Content.Contains("&lt;"), "HTML markup removed");
}

static void WarningIconIsMatchedByHeaderAndUrl()
{
    const string warnings = """{"urunUyariIcerik":[{"baslik":"SAKLAMA KOŞULU","aciklama":"Buzdolabında saklayın.","resim_url":"https://example.test/fridge.svg"}]}""";
    const string icons = """[{"header":"SAKLAMA KOŞULU","source_url":"https://example.test/fridge.svg","icon_base64":"AQID"}]""";
    var result = ProductWarningFormatter.Parse(warnings, icons);
    Assert(result.Count == 1 && result[0].Icon?.SequenceEqual(new byte[] { 1, 2, 3 }) == true, "warning icon mapping");
}

static void CatalogCopyAndStockRangeFormattingIsStable()
{
    Assert(ProductCatalogFormatting.FormatPurchasePriceForClipboard(12.5m) == "12,50", "purchase price clipboard excludes VAT and uses Turkish decimal separator");
    Assert(ProductCatalogFormatting.ParseStockBound(" 25 ") == 25, "stock bound accepts non-negative integer");
    Assert(ProductCatalogFormatting.ParseStockBound("-1") is null, "negative stock bound is ignored");
    Assert(ProductCatalogFormatting.ParseStockBound("abc") is null, "invalid stock bound is ignored");
}

static void RetailSaleCartGroupsAndTotalsItems()
{
    var cart = new RetailSaleCart();
    var first = new RetailSaleItem("QR-1", "8690000000001", false, "ACTIVE", 1, "Test Ürün", 12.50m, 8m, 4, null, null, null);
    var second = first with { EnteredCode = "QR-2" };
    var cosmetic = new RetailSaleItem("8690000000002", "8690000000002", true, "ACTIVE", 2, "Test Kozmetik", 20m, 10m, 3, null, null, null);

    cart.Add(first);
    cart.Add(second);
    cart.Add(cosmetic);
    Assert(cart.Groups.Count == 2, "sale cart groups by barcode");
    Assert(cart.ItemCount == 3 && cart.TotalAmount == 45m, "sale cart totals");
    Assert(cart.ContainsSerializedCode("QR-1"), "serialized duplicate detection");
    Assert(cart.CountForBarcode("8690000000001") == 2, "sale cart barcode count");

    cart.RemoveEntry(cart.Groups[0].Entries[0]);
    Assert(cart.ItemCount == 2 && cart.TotalAmount == 32.50m, "sale cart entry removal");
    cart.RemoveGroup(cart.Groups[0]);
    Assert(cart.Groups.Count == 1 && cart.ItemCount == 1, "sale cart group removal");
}

static void RetailPaymentBalancerMaintainsExactTotal()
{
    var amounts = Enum.GetValues<RetailPaymentKind>().ToDictionary(kind => kind, _ => 0m);
    RetailPaymentBalancer.Rebalance(125.50m, [RetailPaymentKind.Cash], amounts);
    Assert(amounts[RetailPaymentKind.Cash] == 125.50m, "single payment receives full total");

    amounts[RetailPaymentKind.Cash] = 40m;
    RetailPaymentBalancer.Rebalance(
        125.50m,
        [RetailPaymentKind.Cash, RetailPaymentKind.Credit],
        amounts,
        RetailPaymentKind.Cash);
    Assert(amounts[RetailPaymentKind.Credit] == 85.50m, "second selected payment balances edited amount");
    Assert(RetailPaymentBalancer.IsExact(
        125.50m,
        [RetailPaymentKind.Cash, RetailPaymentKind.Credit],
        amounts), "balanced payment total is exact");

    amounts[RetailPaymentKind.Discount] = 10m;
    RetailPaymentBalancer.Rebalance(125.50m, [RetailPaymentKind.Cash], amounts);
    Assert(amounts[RetailPaymentKind.Discount] == 0m, "unchecked payment is cleared");
}

static void RetailMoneyPartsComposeAndSplitAmounts()
{
    Assert(MoneyAmountParts.Compose("125", "5") == 125.50m, "single decimal digit is treated as tenths");
    Assert(MoneyAmountParts.Compose("125", "05") == 125.05m, "two decimal digits are treated as hundredths");
    Assert(MoneyAmountParts.Split(125.5m) == ("125", "50"), "amount splits into compact whole and decimal fields");
}

static void RetailTurkishPatientSearchNormalizesDottedAndDotlessI()
{
    Assert(RetailTurkishSearch.Normalize("  İPEK IŞIK ŞĞÜÖÇ  ") == "ipek ışık şğüöç", "Turkish casing preserves dotted and dotless i");
}

static void RetailCancellationLifetimeIsIdempotent()
{
    CancellationTokenSource? source = new();
    source.Dispose();
    CancellationTokenSourceGuard.CancelAndDispose(ref source);
    CancellationTokenSourceGuard.CancelAndDispose(ref source);
    Assert(source is null, "disposed cancellation source can be safely cancelled repeatedly during unload races");
}

static void RetailRefundPatientResolutionDetectsMixedSales()
{
    Assert(RetailRefundRules.ResolvePatient([42, 42]).PatientId == 42, "same patient is autofilled");
    Assert(RetailRefundRules.ResolvePatient([42, 17]).HasConflict, "different patients require manual selection");
    Assert(RetailRefundRules.ResolvePatient([42, null]).HasConflict, "patient and patientless sales require manual selection");
}

static void RetailRefundPaymentRulesAcceptOnlySupportedExactPayments()
{
    RetailRefundRules.ValidatePayments(100m,
        [new RetailSalePaymentInput(RetailPaymentKind.Cash, 40m), new RetailSalePaymentInput(RetailPaymentKind.Credit, 60m)]);
    var rejected = false;
    try
    {
        RetailRefundRules.ValidatePayments(100m, [new RetailSalePaymentInput(RetailPaymentKind.Debt, 100m)]);
    }
    catch (InvalidOperationException)
    {
        rejected = true;
    }
    Assert(rejected, "refund rejects debt and discount payment kinds");
}

static void RetailQrNormalizerAcceptsScannerFormats()
{
    var scannerText = "]d2(01)08690000000001" + (char)29 + "(21)ABC123 ";
    Assert(
        RetailQrCodeNormalizer.Normalize(scannerText) == "010869000000000121ABC123",
        "scanner prefix, parentheses, group separator and whitespace are normalized");
}

static void RetailMovementAddressUsesPatientRecordId()
{
    Assert(RetailSaleMovementAddress.Format(42) == "P42", "patient record id address");
    Assert(RetailSaleMovementAddress.Format(null) == "P", "patientless address");
}

static void RetailDebtRequiresPatientAndReturnsAmount()
{
    var payments = new[]
    {
        new RetailSalePaymentInput(RetailPaymentKind.Cash, 75m),
        new RetailSalePaymentInput(RetailPaymentKind.Debt, 25m)
    };
    var rejected = false;
    try
    {
        RetailSaleRules.ValidatePayments(100m, payments, false, null);
    }
    catch (InvalidOperationException exception) when (exception.Message.Contains("hasta", StringComparison.OrdinalIgnoreCase))
    {
        rejected = true;
    }
    Assert(rejected, "debt without patient is rejected");
    Assert(RetailSaleRules.ValidatePayments(100m, payments, false, 42) == 25m, "debt amount is returned for patient update");
}

static void RetailQrDiscountAllocationPreservesExactTotal()
{
    var cart = new RetailSaleCart();
    cart.Add(new RetailSaleItem("QR-A", "8691", false, "ACTIVE", 1, "A", 30m, 20m, 1, null, 20m, null));
    cart.Add(new RetailSaleItem("QR-B", "8692", false, "ACTIVE", 2, "B", 70m, 50m, 1, null, 50m, null));
    cart.AllocateDiscount(10m);
    var entries = cart.Groups.SelectMany(group => group.Entries).ToArray();
    Assert(entries[0].DiscountAmount == 3m && entries[1].DiscountAmount == 7m, "discount follows each QR gross-price percentage");
    Assert(entries.Sum(entry => entry.DiscountAmount) == 10m, "rounded QR discounts preserve exact sale discount");
    RetailSaleRules.ValidateLineDiscounts(
        entries.Select(entry => new RetailSaleLineInput(entry.Item, entry.DiscountAmount)).ToArray(),
        10m,
        true);
}

static void RetailRefundDebtReductionIsBounded()
{
    Assert(RetailRefundRules.CalculateDebtReduction(100m, 150m, true) == 100m, "debt reduction cannot exceed return total");
    Assert(RetailRefundRules.CalculateDebtReduction(100m, 35m, true) == 35m, "all smaller patient debt is removed");
    Assert(RetailRefundRules.CalculateDebtReduction(100m, 35m, false) == 0m, "unchecked debt reduction transfers the full amount to payment");
}

static void LoyalPatientMonthlyContinuityIsRecognized()
{
    var today = new DateOnly(2026, 9, 1);
    var visits = Enumerable.Range(0, 12).Select(offset => today.AddMonths(-offset));
    var result = LoyalPatientClassifier.Evaluate(visits, 1m, today);
    Assert(result.IsLoyal, "monthly visits across a year are loyal");
    Assert(result.UniqueVisitDays == 12, "all monthly visit days are retained");
}

static void LoyalPatientSameDayBurstIsRejected()
{
    var today = new DateOnly(2026, 9, 1);
    var result = LoyalPatientClassifier.Evaluate(
        [today, today, today, today.AddDays(-1)], 1m, today);
    Assert(!result.IsLoyal && result.UniqueVisitDays == 2,
        "same-day correction prescriptions count as one visit and cannot create loyalty");
}

static void LoyalPatientLongHistoryToleratesOneMissedInterval()
{
    var today = new DateOnly(2026, 9, 1);
    var visits = Enumerable.Range(0, 37)
        .Where(offset => offset != 17)
        .Select(offset => today.AddMonths(-offset));
    var result = LoyalPatientClassifier.Evaluate(visits, 1m, today);
    Assert(result.IsLoyal, "one missed month does not erase three years of continuous loyalty");
}

static void LoyalPatientLapsedHistoryIsRejected()
{
    var today = new DateOnly(2026, 9, 1);
    var visits = Enumerable.Range(6, 18).Select(offset => today.AddMonths(-offset));
    var result = LoyalPatientClassifier.Evaluate(visits, 1m, today);
    Assert(!result.IsLoyal, "a patient whose regular history stopped months ago is no longer loyal");
}

static void LoyalPatientHalfMonthlyThresholdMeansEveryTwoMonths()
{
    var today = new DateOnly(2026, 9, 1);
    var visits = Enumerable.Range(0, 8).Select(offset => today.AddMonths(-offset * 2));
    var result = LoyalPatientClassifier.Evaluate(visits, 0.5m, today);
    Assert(result.IsLoyal, "0.5 monthly frequency accepts one visit roughly every two months");
}

static void LoyalPatientMedicationHistoryUsesBarcode()
{
    var table = new MedulaRemoteTable(
        "drug-history",
        ["Reçete No", "İlaç Adı", "İlaç Alım Tarihi", "Verilebileceği Tarih", "Adet", "İlaç Kullanımı", "Rap. Teşhisi / Rap. Tak. No"],
        [["3PU0QGL", "ÖRNEK İLAÇ (8699522095650)", "01/08/2026", "01/09/2026", "2", "Günde 2 x 1", "R-42"]],
        Title: "İlaçlar");
    var screen = new MedulaRemoteScreen(true, "drugs", "İlaçlar", "", "", [], [], [table]);
    var row = MedulaLoyalPatientHistoryParser.ParseMedications(screen).Single();
    Assert(row.MedicationBarcode == "8699522095650", "medication history stores the ITS barcode, not the name");
    Assert(row.Quantity == 2m && row.ReportDiagnosisNo == "R-42", "quantity and report/diagnosis reference are parsed");
}

static void LoyalPatientMedicationHistoryKeepsNameWithoutInlineBarcode()
{
    var table = new MedulaRemoteTable(
        "drug-history",
        ["Reçete No", "İlaç Adı", "İlaç Alım Tarihi", "Verilebileceği Tarih", "Adet", "İlaç Kullanımı", "Rap. Teşhisi / Rap. Tak. No"],
        [["3PJVMDJ", "DUTAPROS 0,5 MG 30 YUMUSAK KAPSUL", "14/08/2026", "14/09/2026", "3", "Günde 1 x 1", "R-42"]],
        Title: "İlaçlar");
    var screen = new MedulaRemoteScreen(true, "drugs", "İlaçlar", "", "", [], [], [table]);
    var row = MedulaLoyalPatientHistoryParser.ParseMedications(screen).Single();
    Assert(row.MedicationBarcode.Length == 0
        && row.MedicationName == "DUTAPROS 0,5 MG 30 YUMUSAK KAPSUL",
        "barcode-less Medula rows must preserve the medication name for authoritative resolution.");
}

static void LoyalPatientMedicationBarcodeResolutionUsesPrescription()
{
    var row = new LoyalPatientMedicationHistoryRow(
        "3NH3QKF", "", null, null, 1m, "", "", "TAMPROST MR 0,4 MG 90 KAP");
    var result = LoyalPatientMedicationBarcodeResolver.Resolve(row,
    [
        new("3NH3QKF", "8699502151505", "TAMPROST MR 0,4 MG 90 KAP", false),
        new("3NH3QKF", "8699809156906", "TAMPROST MR 0,4 MG 90 KAPSUL", true),
        new("", "8699809156906", "TAMPROST MR 0,4 MG 90 KAPSUL", false)
    ]);
    Assert(result == "8699502151505",
        "exact medication name within the same prescription must beat an ambiguous catalog name.");
}

static void LoyalPatientReportHistoryUsesRequestedColumns()
{
    var table = new MedulaRemoteTable(
        "reports",
        ["Rapor Takip No", "Rapor No", "Başlangıç Tarihi", "Bitiş Tarihi", "Tanı"],
        [["T-10", "R-20", "01/01/2026", "31/12/2026", "J00"]],
        Title: "Raporlar");
    var screen = new MedulaRemoteScreen(true, "reports", "Raporlar", "", "", [], [], [table]);
    var row = MedulaLoyalPatientHistoryParser.ParseReports(screen, new DateOnly(2026, 9, 1)).Single();
    Assert(row.ReportTrackingNo == "T-10" && row.ReportNo == "R-20" && row.Diagnosis == "J00",
        "active report history keeps tracking number, report number, dates and diagnosis");
}

static void LoyalPatientStrikesRemoveOnThirdFailure()
{
    var first = LoyalPatientStrikePolicy.Apply(true, 0, false);
    var second = LoyalPatientStrikePolicy.Apply(first.IsLoyal, first.StrikeCount, false);
    var third = LoyalPatientStrikePolicy.Apply(second.IsLoyal, second.StrikeCount, false);
    Assert(first.IsLoyal && first.StrikeCount == 1 && !first.Removed,
        "First unloyal result should retain loyalty with one strike.");
    Assert(second.IsLoyal && second.StrikeCount == 2 && !second.Removed,
        "Second unloyal result should retain loyalty with two strikes.");
    Assert(!third.IsLoyal && third.StrikeCount == 3 && third.Removed,
        "Third consecutive unloyal result should remove loyalty.");
}

static void LoyalPatientStrikesResetWhenQualified()
{
    var result = LoyalPatientStrikePolicy.Apply(true, 2, true);
    Assert(result.IsLoyal && result.StrikeCount == 0 && !result.Removed,
        "A loyal result should reset all previous strikes.");
}

static void LoyalPatientDefaultToleranceAcceptsOrdinaryGaps()
{
    var today = new DateOnly(2026, 9, 1);
    var visits = new[] { today.AddMonths(-4), today.AddMonths(-2), today };
    var result = LoyalPatientClassifier.Evaluate(
        visits, new LoyalPatientClassificationOptions(), today);
    Assert(result.IsLoyal,
        "default tolerance recognizes a recurring patient with ordinary missed monthly visits");
}

static void LoyalPatientStrictnessCanBeConfigured()
{
    var today = new DateOnly(2026, 9, 1);
    var visits = new[] { today.AddMonths(-4), today.AddMonths(-2), today };
    var strict = new LoyalPatientClassificationOptions(
        MinimumMonthlyFrequency: 1m,
        MinimumDistinctVisitDays: 3,
        MinimumObservationIntervals: 1.5m,
        FrequencyAcceptanceRatio: 1m,
        RecentVisitGraceIntervals: 2m);
    Assert(!LoyalPatientClassifier.Evaluate(visits, strict, today).IsLoyal,
        "raising the frequency acceptance ratio changes the classification result");
}

static void LoyalPatientReportSupplyRatioMatchesPrescriptionNumbers()
{
    var table = new MedulaRemoteTable(
        "drugs",
        ["Reçete No", "İlaç Adı", "Verilebileceği Tarih", "Rap. Teşhisi/Rap. Tak. No"],
        [
            ["3PU0QGL", "İlaç A", "01/09/2026", "900010"],
            ["3OTHER", "İlaç B", "01/09/2026", "900011"]
        ],
        Title: "İlaçlar");
    var screen = new MedulaRemoteScreen(true, "drugs", "İlaçlar", "", "", [], [], [table]);
    var result = LoyalPatientReportSupplyEvaluator.Evaluate(screen, ["3PU0QGL"], 0.49m, 2);
    Assert(result.TotalReports == 2 && result.SuppliedReports == 1 && result.SupplyRatio == 0.5m,
        "report supply ratio is derived directly from medication-history prescription numbers");
    Assert(result.IsLoyal, "a ratio above the configured threshold is loyal");
}

static void LoyalPatientReportSupplyThresholdIsStrict()
{
    var table = new MedulaRemoteTable(
        "drugs", ["Reçete No", "Verilebileceği Tarih", "Rap. Teşhisi/Rap. Tak. No"],
        [["3PU0QGL", "01/09/2026", "900010"], ["3OTHER", "01/09/2026", "900011"]], Title: "İlaçlar");
    var screen = new MedulaRemoteScreen(true, "drugs", "İlaçlar", "", "", [], [], [table]);
    Assert(!LoyalPatientReportSupplyEvaluator.Evaluate(screen, ["3PU0QGL"], 0.5m, 2).IsLoyal,
        "a ratio equal to the threshold is not loyal because the rule says higher than");
}

static void LoyalPatientReportSampleDelayHonorsJitter()
{
    Assert(LoyalPatientReportSupplyEvaluator.CalculateDelay(3m, 0.2m, 0).TotalSeconds == 2.8,
        "minimum jitter delay is 2.8 seconds");
    Assert(LoyalPatientReportSupplyEvaluator.CalculateDelay(3m, 0.2m, 1).TotalSeconds == 3.2,
        "maximum jitter delay is 3.2 seconds");
}

static void LoyalPatientReportSampleEstimatesPopulation()
{
    Assert(LoyalPatientReportSupplyEvaluator.EstimatePopulationCount(60, 300, 31000) == 6200,
        "a 20 percent sample extrapolates to 6200 of 31000 patients");
}

static void LoyalPatientReportSupplyUsesMedicationHistoryLink()
{
    var medications = new MedulaRemoteScreen(true, "drugs", "İlaçlar", "", "", [], [],
        [new MedulaRemoteTable(
            "drugs",
            ["Reçete No", "İlaç Adı", "İlaç Alım Tarihi", "Verilebileceği Tarih", "Adet", "İlaç Kullanımı", "Rap. Teşhisi / Rap. Tak. No"],
            [["3PU0QGL", "ÖRNEK (8699522095650)", "01/08/2026", "01/09/2026", "1", "Günde 1", "Tanı · 900900"]],
            Title: "İlaçlar")]);
    var result = LoyalPatientReportSupplyEvaluator.Evaluate(
        medications, ["3PU0QGL"], 0.70m, 1);
    Assert(result.TotalReports == 1 && result.SuppliedReports == 1 && result.IsLoyal,
        "medication history directly links a report reference to our prescription number");
}

static void LoyalPatientDiagnosisOnlyRowIsNotReportEvidence()
{
    var medications = new MedulaRemoteScreen(true, "drugs", "İlaçlar", "", "", [], [],
        [new MedulaRemoteTable(
            "drugs", ["Reçete No", "Verilebileceği Tarih", "Rap. Teşhisi / Rap. Tak. No"],
            [["3PU0QGL", "01/09/2026", "E11.9 Diabetes mellitus"]],
            Title: "İlaçlar")]);
    var result = LoyalPatientReportSupplyEvaluator.Evaluate(
        medications, ["3PU0QGL"], 0.70m, 1);
    Assert(!result.HasReportEvidence && result.TotalReports == 0,
        "a diagnosis without a report tracking number is not treated as report evidence");
}

static void LoyalPatientHybridFamilyInheritanceWorksWithoutHistory()
{
    var today = new DateOnly(2026, 9, 8);
    var frequency = LoyalPatientClassifier.Evaluate([], new LoyalPatientClassificationOptions(), today);
    var report = new LoyalPatientReportSupplyEvaluation(0, 0, 0, false, [], false, false);
    var context = new LoyalPatientHybridContext([], 1, 0, false, false, null);
    var result = LoyalPatientReportSupplyEvaluator.EvaluateHybrid(
        frequency, report, context, new LoyalPatientReportSupplyOptions(), today);
    Assert(result.IsLoyal && result.FamilyScore >= 60m,
        "one decided-loyal family member classifies a no-history child or relative as loyal");
}

static void LoyalPatientHybridKeepsMechanism2Positive()
{
    var today = new DateOnly(2026, 9, 8);
    var frequency = LoyalPatientClassifier.Evaluate([], new LoyalPatientClassificationOptions(), today);
    var report = new LoyalPatientReportSupplyEvaluation(1, 1, 1m, true, ["3PU0QGL"], true, false);
    var context = new LoyalPatientHybridContext([], 0, 0, false, false, null);
    var result = LoyalPatientReportSupplyEvaluator.EvaluateHybrid(
        frequency, report, context, new LoyalPatientReportSupplyOptions(), today);
    Assert(result.IsLoyal && result.Mechanism2IsLoyal,
        "Mechanism 3 always retains a patient accepted by Mechanism 2");
}

static void LoyalPatientHybridFallsBackAfterMechanism2Rejection()
{
    var today = new DateOnly(2026, 9, 8);
    var frequency = new LoyalPatientEvaluation(
        true, 12, new DateOnly(2025, 9, 8), today, 1m, 1m, "Mechanism 1 qualified.");
    var report = new LoyalPatientReportSupplyEvaluation(
        3, 0, 0m, false, [], true, true);
    var context = new LoyalPatientHybridContext([], 0, 0, false, false, null);
    var result = LoyalPatientReportSupplyEvaluator.EvaluateHybrid(
        frequency, report, context, new LoyalPatientReportSupplyOptions(), today);
    Assert(result.IsLoyal && result.Mechanism1IsLoyal && !result.Mechanism2IsLoyal,
        "a Mechanism 2 rejection falls through to Mechanism 1 instead of rejecting the patient");
}

static void LoyalPatientHybridRetainsPastReportQualification()
{
    var today = new DateOnly(2026, 9, 8);
    var frequency = LoyalPatientClassifier.Evaluate([], new LoyalPatientClassificationOptions(), today);
    var report = new LoyalPatientReportSupplyEvaluation(0, 0, 0, false, [], false, false);
    var context = new LoyalPatientHybridContext([], 0, 0, true, true, null);
    var result = LoyalPatientReportSupplyEvaluator.EvaluateHybrid(
        frequency, report, context, new LoyalPatientReportSupplyOptions(), today);
    Assert(result.IsLoyal && result.IsReportDecision,
        "a patient previously proven loyal by sufficient report evidence remains loyal if reports disappear");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException($"Assertion failed: {message}");
}

static void ProductAnalysisCalculationsFollowNinetyDayAndShelfStockRules()
{
    Assert(ProductAnalysisCalculations.AverageMonthlySales(9) == 3m,
        "ninety-day sales are divided into three months");
    Assert(ProductAnalysisCalculations.MissingStock(3m, 1.5m, 2) == 3m,
        "missing stock rounds the configured target minus shelf stock upward");
    Assert(ProductAnalysisCalculations.MissingStock(3m, 1.5m, 8) == -3m,
        "negative stock difference also uses mathematical upward rounding");
    Assert(ProductAnalysisCalculations.StockComment(-3.5m) == "İade Edebilirsin" &&
           ProductAnalysisCalculations.StockComment(-3m) == "stok fazlası" &&
           ProductAnalysisCalculations.StockComment(-0.5m) == "stok fazlası" &&
           ProductAnalysisCalculations.StockComment(0m) == "stok yeterli" &&
           ProductAnalysisCalculations.StockComment(2.5m) == "alma" &&
           ProductAnalysisCalculations.StockComment(3m) == "al",
        "signed stock difference maps to the requested recommendation boundaries");
    Assert(ProductAnalysisCalculations.IsValidWantedStockMonths(0.5m) &&
           ProductAnalysisCalculations.IsValidWantedStockMonths(2.5m) &&
           !ProductAnalysisCalculations.IsValidWantedStockMonths(1.2m),
        "wanted stock months accept only half-month increments");

    var movementTypes = new[]
    {
        new ProductAnalysisMovementType("PRESC_OUT", "Reçeteli Satış", false),
        new ProductAnalysisMovementType("PERAK_OUT", "Perakende Satış", false),
        new ProductAnalysisMovementType("DEP_IN", "Depodan Alış", true)
    };
    var day = new ProductAnalysisMovementDay(new DateTime(2026, 9, 1),
        new Dictionary<string, int> { ["PRESC_OUT"] = 3, ["PERAK_OUT"] = 2, ["DEP_IN"] = 6 });
    var analysis = new ProductAnalysisData(0, 1.5m, 0, 0, movementTypes, [day]);
    Assert(analysis.QuantityFor(day, new ProductAnalysisMovementType(null, "Tüm Çıkışlar", false)) == 5 &&
           analysis.QuantityFor(day, movementTypes[0]) == 3 &&
           analysis.QuantityFor(day, new ProductAnalysisMovementType(null, "Tüm Girişler", true)) == 6,
        "daily movement selectors separate individual types and aggregate each direction");
}

static void ConsignmentSaleContractUsesReservedStockAndUnifiedType()
{
    Assert(RetailStockStatus.IsConsigned("RESERVED"), "reserved QR status is blocked from ordinary sales");
    Assert(RetailStockStatus.IsConsigned("emanet"), "legacy emanet QR status remains blocked");
    Assert(!RetailStockStatus.IsConsigned(null), "ordinary active stock is not consigned");
    var launch = RetailSaleLaunchOptions.SettleConsignment(42);
    Assert(launch.Mode == RetailSaleMode.ConsignmentSettlement && launch.ConsignmentSaleId == 42,
        "settlement launch keeps the owning consignment id");
}

static void ConsignmentSettlementShowsOnlyOutstandingCollection()
{
    var original = new Dictionary<RetailPaymentKind, decimal>
    {
        [RetailPaymentKind.Cash] = 10m,
        [RetailPaymentKind.Credit] = 0m,
        [RetailPaymentKind.BankTransfer] = 0m,
        [RetailPaymentKind.Debt] = 54.77m,
        [RetailPaymentKind.Discount] = 0m
    };
    var collection = new[] { new RetailSalePaymentInput(RetailPaymentKind.Cash, 54.77m) };
    var final = ConsignmentSettlementPayments.ComposeFinalPayments(original, collection);

    Assert(ConsignmentSettlementPayments.CollectionTarget(original) == 54.77m,
        "settlement UI asks only for the outstanding consignment debt");
    Assert(final.Single(payment => payment.Kind == RetailPaymentKind.Cash).Amount == 64.77m,
        "final sale accounting retains the earlier cash payment and adds the new collection");
    Assert(final.All(payment => payment.Kind != RetailPaymentKind.Debt),
        "settlement clears debt instead of copying it to the final retail sale");
}

static void ConsignmentCancellationIsASeparateEntryTransaction()
{
    var cancellation = new RetailSaleListItem
    {
        SaleId = 1,
        ReferenceCode = "E1",
        TransactionType = RetailTransactionTypes.ConsignmentCancellation,
        MedicineQuantity = 1,
        Timestamp = DateTime.Today
    };
    var consignment = new RetailSaleListItem
    {
        SaleId = 1,
        ReferenceCode = "E1",
        TransactionType = RetailTransactionTypes.ConsignmentSale,
        MedicineQuantity = 1,
        Timestamp = DateTime.Today
    };

    Assert(RetailTransactionTypes.All.HasFlag(RetailTransactionTypes.ConsignmentCancellation),
        "all transaction types include consignment cancellations");
    Assert(cancellation.TransactionTypeDisplay == "Emanet İptal",
        "returned consignments use their own display type");
    Assert(cancellation.IsEntryDirection && !consignment.IsEntryDirection,
        "split view puts the cancellation in entries and the original consignment in exits");
}

static void AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"Assertion failed: {message}");
}

sealed class FakeItsHandler : HttpMessageHandler
{
    public List<(string Path, string Authorization, string Body)> Calls { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add((
            request.RequestUri?.AbsolutePath ?? string.Empty,
            request.Headers.Authorization?.ToString() ?? string.Empty,
            body));
        var responseBody = Calls.Count == 1
            ? "{\"token\":\"offline-token\"}"
            : "{\"notificationid\":\"4242\",\"productList\":[{\"gtin\":\"08699536090115\",\"sn\":\"2674000056097596\",\"uc\":\"00000\"}]}";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody)
        };
    }
}

sealed class FakeItsStatusHandler : HttpMessageHandler
{
    public List<(string Path, string Authorization, string Body)> Calls { get; } = [];
    public int RemainingStatusFailures { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        Calls.Add((path,
            request.Headers.Authorization?.ToString() ?? string.Empty, body));
        if (path == "/token/app/token/")
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"token\":\"offline-token\"}")
            };
        if (RemainingStatusFailures > 0)
        {
            RemainingStatusFailures--;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{\"fc\":\"50300\",\"fm\":\"temporary\"}")
            };
        }
        if (path == "/reference/app/errorcode/")
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"errorCodeList\":[{\"code\":\"60014\",\"message\":\"Ürün eczane stokunda\",\"description\":\"Ürün sorgulayan eczanenin stokundadır\"}]}")
            };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"notificationid\":\"9001\",\"responseObjectList\":[{\"gln1\":\"8680001000012\",\"gln2\":\"\",\"gtin\":\"08699578091026\",\"sn\":\"00000024114179\",\"uc\":\"60014\"}]}")
        };
    }
}

sealed class FakeItsReferenceHandler : HttpMessageHandler
{
    public List<(string Path, string Body)> Calls { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add((path, body));
        var responseBody = path switch
        {
            "/token/app/token/" => "{\"token\":\"offline-token\"}",
            "/reference/app/errorcode/" =>
                "{\"errorCodeList\":[{\"code\":\"60014\",\"message\":\"Kayıtlı\",\"description\":\"Ürün eczane stokunda\"}]}",
            "/reference/app/drug/" =>
                "{\"drugList\":[{\"gtin\":\"08699536090115\",\"drugName\":\"Örnek İlaç\",\"manufacturerGln\":\"8680001000012\",\"manufacturerName\":\"Örnek Üretici\",\"active\":true,\"imported\":true}]}",
            _ => "{}"
        };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseBody) };
    }
}

sealed class FakeItsSaleErrorHandler : HttpMessageHandler
{
    public List<(string Path, string Body)> Calls { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add((path, body));
        var responseBody = path switch
        {
            "/token/app/token/" => "{\"token\":\"offline-token\"}",
            "/prescription/app/pharmacysale/" =>
                "{\"notificationid\":\"99\",\"productList\":[{\"gtin\":\"08699536090115\",\"sn\":\"SOLD-SERIAL\",\"uc\":\"10204\"}]}",
            "/reference/app/errorcode/" =>
                "{\"errorCodeList\":[{\"code\":\"10204\",\"message\":\"Ürün daha önce satılmış\",\"description\":\"Satış bildirimi yinelenemez\"}]}",
            _ => "{}"
        };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseBody) };
    }
}

sealed class FakeLocalProductStatusSource(LocalQrStatus result) : ILocalProductStatusSource
{
    public Task<LocalQrStatus> QueryAsync(string enteredQrCode, CancellationToken cancellationToken) =>
        Task.FromResult(result);
}

sealed class FailingItsStatusClient : IItsProductStatusClient
{
    public Task<ItsStatusQueryResult> QueryProductStatusAsync(
        ItsEnvironment environment,
        ItsCredentials credentials,
        ItsStatusQueryRequest request,
        CancellationToken cancellationToken) =>
        throw new HttpRequestException("offline status query");
}

sealed class FailingAcceptanceHandler : HttpMessageHandler
{
    private int callCount;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        callCount++;
        if (callCount == 1)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"token\":\"offline-token\"}")
            });
        }

        throw new HttpRequestException(
            HttpRequestError.ConnectionError,
            "Simulated offline connection.",
            null);
    }
}

sealed class FakeRetailItsClient(
    Func<ItsPharmacyNotificationRequest, ItsNotificationResult> send)
    : IItsPharmacyNotificationClient
{
    public Task<ItsNotificationResult> SendNotificationAsync(
        ItsEnvironment environment,
        ItsPharmacyOperation operation,
        ItsCredentials credentials,
        ItsPharmacyNotificationRequest request,
        CancellationToken cancellationToken) => Task.FromResult(send(request));
}

sealed class FakeInvoiceItsReturnClient : IInvoiceItsReturnClient
{
    public int CallCount { get; private set; }
    public ItsEnvironment Environment { get; private set; }
    public ItsReturnRequest? Request { get; private set; }

    public Task<ItsNotificationResult> SendReturnAsync(
        ItsEnvironment environment,
        ItsCredentials credentials,
        ItsReturnRequest request,
        CancellationToken cancellationToken)
    {
        CallCount++;
        Environment = environment;
        Request = request;
        return Task.FromResult(new ItsNotificationResult(
            200,
            "{}",
            "partial-return-1",
            null,
            null,
            request.Products.Select(product => new ItsProductResult(
                product.Gtin,
                product.SerialNumber,
                "00000")).ToArray()));
    }
}

sealed class FakeRetailItsStore : IRetailItsOperationStore
{
    public List<string> Events { get; } = [];

    public Task<Guid> PrepareAsync(
        RetailItsOperationKind operationKind,
        string pharmacyGln,
        ItsPharmacyNotificationRequest request,
        IReadOnlyList<string> qrCodes,
        string requestedBy,
        CancellationToken cancellationToken)
    {
        Events.Add("prepared");
        return Task.FromResult(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    }

    public Task MarkRemoteConfirmedAsync(Guid operationId, ItsNotificationResult result, CancellationToken cancellationToken)
    {
        Events.Add("remote_confirmed");
        return Task.CompletedTask;
    }

    public Task MarkDefinitelyNotSentAsync(Guid operationId, string error, CancellationToken cancellationToken)
    {
        Events.Add("definitely_not_sent");
        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(
        Guid operationId,
        ItsNotificationResult result,
        string error,
        CancellationToken cancellationToken)
    {
        Events.Add("failed");
        return Task.CompletedTask;
    }

    public Task MarkReconciliationRequiredAsync(
        Guid operationId,
        ItsNotificationResult? result,
        string error,
        CancellationToken cancellationToken)
    {
        Events.Add("reconciliation_required");
        return Task.CompletedTask;
    }

    public Task MarkLocalCompletedAsync(
        Guid operationId,
        RetailItsOperationKind operationKind,
        int localRecordId,
        CancellationToken cancellationToken)
    {
        Events.Add("local_completed");
        return Task.CompletedTask;
    }
}
