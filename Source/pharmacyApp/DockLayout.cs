using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace pharmacyApp;

internal sealed class PanelItem
{
    private static int nextZ;

    public PanelItem(
        string name,
        FrameworkElement content,
        double minimumWidth,
        double minimumHeight,
        string? moduleId = null,
        Func<FrameworkElement>? createTabIcon = null)
    {
        Name = name;
        Content = content;
        MinimumWidth = minimumWidth;
        MinimumHeight = minimumHeight;
        ModuleId = moduleId;
        CreateTabIcon = createTabIcon;
        ZOrder = ++nextZ;
        var margin = Content.Margin;
        Content.MinWidth = Math.Max(0, minimumWidth - margin.Left - margin.Right);
        Content.MinHeight = Math.Max(0, minimumHeight - margin.Top - margin.Bottom);
        Content.PreviewMouseLeftButtonDown += (_, _) => FocusRequested?.Invoke(this);
    }

    public string Name { get; private set; }
    public FrameworkElement Content { get; }
    public double MinimumWidth { get; }
    public double MinimumHeight { get; }
    public string? ModuleId { get; private set; }
    public Func<FrameworkElement>? CreateTabIcon { get; }
    public int ZOrder { get; private set; }
    public bool IsFocused { get; private set; }
    public event Action<PanelItem>? LayerChanged;
    public event Action<PanelItem>? FocusRequested;
    private TextBlock? headerTitle;

    public void BringToFront()
    {
        ZOrder = ++nextZ;
        LayerChanged?.Invoke(this);
    }

    public void SendToBack()
    {
        ZOrder = rootBackZ--;
        LayerChanged?.Invoke(this);
    }

    public void AttachHeaderTitle(TextBlock title)
    {
        headerTitle = title;
        UpdateFocusVisual();
    }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || string.Equals(Name, name, StringComparison.Ordinal)) return;
        Name = name;
        if (headerTitle is not null) headerTitle.Text = name;
    }

    public void ChangeModuleId(string moduleId)
    {
        if (!string.IsNullOrWhiteSpace(moduleId)) ModuleId = moduleId;
    }

    internal void SetFocused(bool focused)
    {
        IsFocused = focused;
        UpdateFocusVisual();
    }

    private void UpdateFocusVisual()
    {
        if (headerTitle is not null)
            headerTitle.FontWeight = IsFocused ? FontWeights.Bold : FontWeights.Normal;
    }

    private static int rootBackZ = -1;
}

internal abstract class PanelLayoutNode
{
    public abstract Size MinimumSize { get; }
    public abstract IEnumerable<PanelItem> Panels { get; }
}

internal sealed class PanelLeaf(PanelItem panel) : PanelLayoutNode
{
    public PanelItem Panel { get; } = panel;
    public override Size MinimumSize => new(Panel.MinimumWidth, Panel.MinimumHeight);
    public override IEnumerable<PanelItem> Panels { get { yield return Panel; } }
}

internal enum SplitOverlapSide { None, First, Second }

internal sealed class PanelSplit(Orientation orientation, PanelLayoutNode first, PanelLayoutNode second) : PanelLayoutNode
{
    private const double SplitterThickness = 7;

    public Orientation Orientation { get; } = orientation;
    public PanelLayoutNode First { get; } = first;
    public PanelLayoutNode Second { get; } = second;
    public double BoundaryRatio { get; set; } = 0.5;
    public SplitOverlapSide OverlapSide { get; set; }

    public override Size MinimumSize
    {
        get
        {
            var firstMinimum = First.MinimumSize;
            var secondMinimum = Second.MinimumSize;
            return Orientation == Orientation.Horizontal
                ? new Size(firstMinimum.Width + secondMinimum.Width + SplitterThickness, Math.Max(firstMinimum.Height, secondMinimum.Height))
                : new Size(Math.Max(firstMinimum.Width, secondMinimum.Width), firstMinimum.Height + secondMinimum.Height + SplitterThickness);
        }
    }

    public override IEnumerable<PanelItem> Panels => First.Panels.Concat(Second.Panels);
}

internal sealed class DockSurface : Canvas
{
    private const double SplitterThickness = 7;
    private const double HeaderPeek = 44;
    private const double SnapDistance = 12;
    private readonly PanelLayoutNode root;
    private readonly Dictionary<PanelSplit, Thumb> splitters = [];
    private readonly Dictionary<PanelSplit, Rect> splitBounds = [];

    public DockSurface(PanelLayoutNode root)
    {
        this.root = root;
        Background = new SolidColorBrush(Color.FromRgb(244, 247, 249));
        ClipToBounds = true;

        foreach (var panel in root.Panels)
        {
            Detach(panel.Content);
            panel.LayerChanged += PanelLayerChanged;
            panel.FocusRequested += FocusPanel;
            Children.Add(panel.Content);
        }
        var initialFocus = root.Panels.Where(panel => panel.IsFocused).OrderByDescending(panel => panel.ZOrder).FirstOrDefault()
            ?? root.Panels.OrderByDescending(panel => panel.ZOrder).First();
        foreach (var panel in root.Panels) panel.SetFocused(ReferenceEquals(panel, initialFocus));

        AddSplitters(root);
        SizeChanged += (_, _) => ArrangeLayout();
        Loaded += (_, _) => ArrangeLayout();
        Unloaded += (_, _) =>
        {
            foreach (var panel in root.Panels)
            {
                panel.LayerChanged -= PanelLayerChanged;
                panel.FocusRequested -= FocusPanel;
            }
        };
    }

    private void AddSplitters(PanelLayoutNode node)
    {
        if (node is not PanelSplit split) return;

        var thumb = new Thumb
        {
            Background = new SolidColorBrush(Color.FromRgb(188, 200, 207)),
            Opacity = 0.9,
            Cursor = split.Orientation == Orientation.Horizontal ? Cursors.SizeWE : Cursors.SizeNS
        };
        thumb.DragDelta += (_, e) => MoveBoundary(split, split.Orientation == Orientation.Horizontal ? e.HorizontalChange : e.VerticalChange);
        splitters.Add(split, thumb);
        Children.Add(thumb);
        AddSplitters(split.First);
        AddSplitters(split.Second);
    }

    private void MoveBoundary(PanelSplit split, double delta)
    {
        if (!splitBounds.TryGetValue(split, out var bounds)) return;
        var length = split.Orientation == Orientation.Horizontal ? bounds.Width : bounds.Height;
        if (length <= 0) return;

        var origin = split.Orientation == Orientation.Horizontal ? bounds.Left : bounds.Top;
        var firstMinimum = split.Orientation == Orientation.Horizontal ? split.First.MinimumSize.Width : split.First.MinimumSize.Height;
        var secondMinimum = split.Orientation == Orientation.Horizontal ? split.Second.MinimumSize.Width : split.Second.MinimumSize.Height;
        var regularStart = origin + firstMinimum;
        var regularEnd = origin + length - secondMinimum;
        var modelBoundary = origin + split.BoundaryRatio * length;
        var controlBoundary = GetControlBoundary(split, modelBoundary, regularStart, regularEnd);
        var next = controlBoundary + delta;

        if (Math.Abs(next - regularStart) <= SnapDistance) next = regularStart;
        if (Math.Abs(next - regularEnd) <= SnapDistance) next = regularEnd;
        next = Math.Clamp(next, origin + HeaderPeek, Math.Max(origin + HeaderPeek, origin + length - HeaderPeek));
        split.BoundaryRatio = (next - origin) / length;
        ArrangeLayout();
    }

    private void ArrangeLayout()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        splitBounds.Clear();
        ArrangeNode(root, new Rect(0, 0, ActualWidth, ActualHeight));
        foreach (var panel in root.Panels)
            System.Windows.Controls.Panel.SetZIndex(panel.Content, panel.ZOrder);
        foreach (var pair in splitters)
            System.Windows.Controls.Panel.SetZIndex(pair.Value, pair.Key.Panels.Max(panel => panel.ZOrder) + 1);
    }

    private void ArrangeNode(PanelLayoutNode node, Rect bounds)
    {
        if (node is PanelLeaf leaf)
        {
            var width = Math.Max(leaf.Panel.MinimumWidth, bounds.Width);
            var height = Math.Max(leaf.Panel.MinimumHeight, bounds.Height);
            var margin = leaf.Panel.Content.Margin;
            leaf.Panel.Content.Width = Math.Max(0, width - margin.Left - margin.Right);
            leaf.Panel.Content.Height = Math.Max(0, height - margin.Top - margin.Bottom);
            SetLeft(leaf.Panel.Content, bounds.Left);
            SetTop(leaf.Panel.Content, bounds.Top);
            return;
        }

        var split = (PanelSplit)node;
        splitBounds[split] = bounds;
        var horizontal = split.Orientation == Orientation.Horizontal;
        var length = horizontal ? bounds.Width : bounds.Height;
        var origin = horizontal ? bounds.Left : bounds.Top;
        var end = origin + length;
        var firstMinimum = horizontal ? split.First.MinimumSize.Width : split.First.MinimumSize.Height;
        var secondMinimum = horizontal ? split.Second.MinimumSize.Width : split.Second.MinimumSize.Height;
        var boundary = origin + split.BoundaryRatio * length;
        var regularStart = origin + firstMinimum;
        var regularEnd = end - secondMinimum;
        Rect firstBounds;
        Rect secondBounds;
        SplitOverlapSide overlap;

        if (regularStart <= regularEnd && boundary >= regularStart && boundary <= regularEnd)
        {
            overlap = SplitOverlapSide.None;
            firstBounds = horizontal
                ? new Rect(bounds.Left, bounds.Top, boundary - bounds.Left, bounds.Height)
                : new Rect(bounds.Left, bounds.Top, bounds.Width, boundary - bounds.Top);
            secondBounds = horizontal
                ? new Rect(boundary, bounds.Top, bounds.Right - boundary, bounds.Height)
                : new Rect(bounds.Left, boundary, bounds.Width, bounds.Bottom - boundary);
        }
        else if (boundary > regularEnd)
        {
            overlap = SplitOverlapSide.First;
            firstBounds = horizontal
                ? new Rect(bounds.Left, bounds.Top, boundary - bounds.Left, bounds.Height)
                : new Rect(bounds.Left, bounds.Top, bounds.Width, boundary - bounds.Top);
            secondBounds = horizontal
                ? new Rect(bounds.Right - secondMinimum, bounds.Top, secondMinimum, bounds.Height)
                : new Rect(bounds.Left, bounds.Bottom - secondMinimum, bounds.Width, secondMinimum);
        }
        else
        {
            overlap = SplitOverlapSide.Second;
            firstBounds = horizontal
                ? new Rect(bounds.Left, bounds.Top, firstMinimum, bounds.Height)
                : new Rect(bounds.Left, bounds.Top, bounds.Width, firstMinimum);
            secondBounds = horizontal
                ? new Rect(boundary, bounds.Top, bounds.Right - boundary, bounds.Height)
                : new Rect(bounds.Left, boundary, bounds.Width, bounds.Bottom - boundary);
        }

        var overlapChanged = split.OverlapSide != overlap;
        split.OverlapSide = overlap;
        if (overlapChanged && overlap != SplitOverlapSide.None)
        {
            foreach (var panel in (overlap == SplitOverlapSide.First ? split.First : split.Second).Panels)
                panel.BringToFront();
        }

        ArrangeNode(split.First, firstBounds);
        ArrangeNode(split.Second, secondBounds);
        var controlBoundary = GetControlBoundary(split, boundary, regularStart, regularEnd);
        ArrangeSplitter(split, bounds, controlBoundary);
    }

    private static double GetControlBoundary(PanelSplit split, double modelBoundary, double regularStart, double regularEnd)
    {
        var firstZ = split.First.Panels.Max(panel => panel.ZOrder);
        var secondZ = split.Second.Panels.Max(panel => panel.ZOrder);
        return split.OverlapSide switch
        {
            SplitOverlapSide.First when secondZ > firstZ => regularEnd,
            SplitOverlapSide.Second when firstZ > secondZ => regularStart,
            _ => modelBoundary
        };
    }

    public void FocusPanel(PanelItem panel)
    {
        if (!root.Panels.Contains(panel)) return;
        foreach (var candidate in root.Panels) candidate.SetFocused(ReferenceEquals(candidate, panel));
        panel.BringToFront();
        ArrangeLayout();
    }

    private void PanelLayerChanged(PanelItem panel)
    {
        if (Children.Contains(panel.Content)) ArrangeLayout();
    }

    private void ArrangeSplitter(PanelSplit split, Rect bounds, double boundary)
    {
        var thumb = splitters[split];
        if (split.Orientation == Orientation.Horizontal)
        {
            thumb.Width = SplitterThickness;
            thumb.Height = bounds.Height;
            SetLeft(thumb, boundary - SplitterThickness / 2);
            SetTop(thumb, bounds.Top);
        }
        else
        {
            thumb.Width = bounds.Width;
            thumb.Height = SplitterThickness;
            SetLeft(thumb, bounds.Left);
            SetTop(thumb, boundary - SplitterThickness / 2);
        }
    }

    private static void Detach(FrameworkElement element)
    {
        switch (element.Parent)
        {
            case Panel panel:
                panel.Children.Remove(element);
                break;
            case ContentControl content when ReferenceEquals(content.Content, element):
                content.Content = null;
                break;
            case Decorator decorator when ReferenceEquals(decorator.Child, element):
                decorator.Child = null;
                break;
        }
    }
}
