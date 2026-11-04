using System.Windows;
using System.Windows.Controls;

namespace pharmacyApp.Modules.Settings;

public sealed class SettingsPanel : Panel
{
    private int Columns(double width) => Math.Max(1, Math.Min(InternalChildren.Count,
        double.IsInfinity(width) ? 1 : (int)(width / 420)));

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 660 : availableSize.Width;
        var columns = Columns(width);
        var rowHeight = 0d;
        var height = 0d;
        for (var index = 0; index < InternalChildren.Count; index++)
        {
            var child = InternalChildren[index];
            child.Measure(new Size(width / columns, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if (index % columns == columns - 1 || index == InternalChildren.Count - 1)
            {
                height += rowHeight;
                rowHeight = 0;
            }
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Columns(finalSize.Width);
        var y = 0d;
        for (var first = 0; first < InternalChildren.Count; first += columns)
        {
            var count = Math.Min(columns, InternalChildren.Count - first);
            var rowHeight = Enumerable.Range(first, count).Max(i => InternalChildren[i].DesiredSize.Height);
            for (var index = 0; index < count; index++)
                InternalChildren[first + index].Arrange(new Rect(index * finalSize.Width / columns, y,
                    finalSize.Width / columns, rowHeight));
            y += rowHeight;
        }
        return finalSize;
    }
}
