namespace pharmacyApp.AI.Tools.Medula;

public sealed class MedulaAiToolProvider : IAiToolProvider
{
    public IReadOnlyCollection<IAiTool> Tools { get; } = MedulaAiTools.Create().ToArray();
}
