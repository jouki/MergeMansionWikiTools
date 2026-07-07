using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace MergeMansionWikiTools.Helpers;

/// <summary>
/// A lightweight drag ghost: renders a single child visual (e.g. a board tile preview) that follows
/// the cursor during a drag operation. Positioned by the drop-target's DragOver handler via
/// <see cref="SetPosition"/>. Hit-test invisible so it never interferes with drop targeting.
/// </summary>
public sealed class DragAdorner : Adorner
{
    private readonly UIElement _child;
    private Point _offset;

    public DragAdorner(UIElement adornedElement, UIElement child) : base(adornedElement)
    {
        _child = child;
        IsHitTestVisible = false;
        AddVisualChild(_child);
    }

    /// <summary>Top-left position of the ghost, in the adorned element's coordinate space.</summary>
    public void SetPosition(Point position)
    {
        _offset = position;
        var layer = AdornerLayer.GetAdornerLayer(AdornedElement);
        layer?.Update(AdornedElement);
        InvalidateArrange();
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _child;

    protected override Size MeasureOverride(Size constraint)
    {
        _child.Measure(constraint);
        return _child.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // Offset by a small amount so the ghost sits just off the cursor tip.
        _child.Arrange(new Rect(new Point(_offset.X + 6, _offset.Y + 6), _child.DesiredSize));
        return finalSize;
    }
}
