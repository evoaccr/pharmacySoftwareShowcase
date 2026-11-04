using System.Text.Json;

namespace pharmacyApp.AI.Workflows;

public sealed class MultiDimensionMovementWorkflow : IAiWorkflow
{
    public AiWorkflowDefinition Definition { get; } = new(
        "hareket_coklu_dagilim_analizi", "Hareketleri birden fazla boyutta analiz et",
        "Runs deterministic QR movement aggregations for two or more requested dimensions and combines the labelled results without another model call.",
        RequiredTools: ["qr.aggregate_movements"],
        Parameters: new Dictionary<string, string>
        {
            ["dimensions"] = "Comma-separated address, user, movement_type, barcode, day, or hour.",
            ["period_days"] = "Relative period in days.", ["start_date"] = "Inclusive YYYY-MM-DD.",
            ["end_date"] = "Inclusive YYYY-MM-DD.", ["direction"] = "entry or exit.",
            ["address_type"] = "depot or patient.", ["movement_types"] = "Configured movement descriptions.",
            ["limit"] = "Maximum rows per dimension, 1-100."
        });

    public async Task<AiWorkflowStepResult> ExecuteAsync(
        AiWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var dimensions = WorkflowData.List(context, "dimensions")
            .Select(value => value.ToLowerInvariant())
            .Where(value => value is "address" or "user" or "movement_type" or "barcode" or "day" or "hour")
            .Distinct(StringComparer.Ordinal).Take(4).ToArray();
        if (dimensions.Length < 2)
            dimensions = ["address", "user"];

        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (var dimension in dimensions)
        {
            var parameters = new Dictionary<string, string>(context.Parameters ?? new Dictionary<string, string>(), StringComparer.Ordinal)
            {
                ["group_by"] = dimension
            };
            parameters.Remove("dimensions");
            var result = await new MovementAggregateWorkflow().ExecuteAsync(
                context with { Parameters = parameters }, cancellationToken);
            if (result.Status != AiWorkflowStepStatus.Completed) return result;
            foreach (var row in ExtendedWorkflowData.Rows(result.Output))
            {
                var labelled = new Dictionary<string, string>(row, StringComparer.Ordinal)
                {
                    ["analysis_dimension"] = dimension switch
                    {
                        "address" => "Depo / Adres", "user" => "Kullanıcı",
                        "movement_type" => "Hareket Türü", "barcode" => "Barkod",
                        "day" => "Gün", "hour" => "Saat", _ => dimension
                    }
                };
                rows.Add(labelled);
            }
        }
        return new AiWorkflowStepResult(
            AiWorkflowStepStatus.Completed,
            $"Karekod hareketleri {dimensions.Length} ayrı boyutta özetlendi.",
            WorkflowData.RowsOutput(rows, new
            {
                total_count = rows.Count,
                visible_columns = new[] { "analysis_dimension", "group_label", "movement_count", "entry_count", "exit_count", "distinct_qr_count" }
            }));
    }
}
