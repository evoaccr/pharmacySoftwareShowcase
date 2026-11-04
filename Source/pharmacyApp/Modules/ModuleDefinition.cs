using System.Windows;

namespace pharmacyApp.Modules;

public interface IModuleTitleSource
{
    string CurrentModuleTitle { get; }
    event Action<string>? ModuleTitleChanged;
}

public interface IModuleIdentitySource
{
    string CurrentModuleId { get; }
    event Action<string>? ModuleIdentityChanged;
}

public sealed record ModulePanelRequest(
    string ModuleId,
    string Title,
    Func<FrameworkElement> CreateContent,
    double MinimumWidth,
    double MinimumHeight,
    ModulePanelPlacement Placement = ModulePanelPlacement.Right,
    string? AnchorModuleId = null,
    Func<FrameworkElement>? CreateTabIcon = null);

public interface IModulePanelRequestSource
{
    event Action<ModulePanelRequest>? PanelRequested;
}

public sealed record ModuleTabRequest(
    string ModuleId,
    string Title,
    Func<FrameworkElement> CreateContent,
    double MinimumWidth,
    double MinimumHeight,
    Func<FrameworkElement>? CreateTabIcon = null);

public interface IModuleTabRequestSource
{
    event Action<ModuleTabRequest>? TabRequested;
}

public enum ModuleWorkspaceLayout
{
    SideBySide,
    Stacked
}

public sealed record ModuleWorkspaceRequest(
    IReadOnlyList<string> ModuleIds,
    string? TabTitle = null,
    ModuleWorkspaceLayout Layout = ModuleWorkspaceLayout.SideBySide);

public interface IModuleWorkspaceRequestSource
{
    event Action<ModuleWorkspaceRequest>? WorkspaceRequested;
}

public interface IModuleCloseRequestSource
{
    event Action? CloseRequested;
}

/// <summary>
/// Implemented by modules that continue long-running work while their tab is not selected.
/// The shell uses this signal only to present activity in the tab strip; the module owns
/// its task lifetime, cancellation, and progress state.
/// </summary>
public interface IModuleBackgroundWorkSource
{
    bool IsBackgroundWorkRunning { get; }
    event Action<bool>? BackgroundWorkStateChanged;
}

public interface IPanelHostModeAware
{
    void SetHostedInPopout(bool isPopout);
}

public interface IModuleTabContextAware
{
    void SetMedicationListAvailableInCurrentTab(bool isAvailable);
}

public enum ModulePanelPlacement
{
    Left,
    Right,
    Top,
    Bottom
}

public sealed record ModulePanelDefinition(
    string Title,
    Func<FrameworkElement> CreateContent,
    double MinimumWidth,
    double MinimumHeight,
    ModulePanelPlacement Placement = ModulePanelPlacement.Right);

public sealed record ModuleLaunchDefinition(
    string TabTitle,
    IReadOnlyList<ModulePanelDefinition> Panels)
{
    public static ModuleLaunchDefinition SinglePanel(
        string tabTitle,
        Func<FrameworkElement> createContent,
        double minimumWidth,
        double minimumHeight) =>
        new(tabTitle, [new ModulePanelDefinition(tabTitle, createContent, minimumWidth, minimumHeight)]);
}

public sealed record ModuleDefinition(
    string Id,
    IReadOnlyList<string> MenuPath,
    string ButtonText,
    Func<ModuleLaunchDefinition> Launch,
    Func<FrameworkElement>? CreateTabIcon = null);

public sealed class ModuleRegistry
{
    private readonly List<ModuleDefinition> modules = [];

    public IReadOnlyList<ModuleDefinition> Modules => modules;

    public void Register(ModuleDefinition module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (string.IsNullOrWhiteSpace(module.Id)) throw new ArgumentException("A module must have a stable ID.", nameof(module));
        if (module.MenuPath.Count == 0 || module.MenuPath.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A module must have at least one non-empty top-menu path segment.", nameof(module));
        if (string.IsNullOrWhiteSpace(module.ButtonText)) throw new ArgumentException("A module button must have visible text.", nameof(module));
        if (modules.Any(existing => string.Equals(existing.Id, module.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A module with ID '{module.Id}' is already registered.");
        modules.Add(module);
    }
}
