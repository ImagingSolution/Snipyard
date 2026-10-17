using System.Collections.Generic;
using System.Linq;
using Avalonia.Layout;

namespace Snipyard.Controls;

/// <summary>
/// One node of a window's dock tree. The tree, not a set of coordinates, is what says where each
/// MDI window sits: a node is either a <see cref="DockSplitNode"/> holding other nodes side by
/// side, or a <see cref="DockLeafNode"/> pane holding the windows themselves.
/// </summary>
internal abstract class DockNode
{
    /// <summary>The split this node hangs off, or null at the root.</summary>
    public DockSplitNode? Parent { get; internal set; }
}

/// <summary>
/// Children laid out in a row (<see cref="Orientation.Horizontal"/>) or a column
/// (<see cref="Orientation.Vertical"/>), with a draggable boundary between each pair.
/// <see cref="Ratios"/> runs parallel to <see cref="Children"/> and sums to 1.
/// </summary>
internal sealed class DockSplitNode : DockNode
{
    public Orientation Orientation { get; set; } = Orientation.Horizontal;

    public List<DockNode> Children { get; } = new();

    /// <summary>Each child's share of the split. Written back after a boundary is dragged.</summary>
    public List<double> Ratios { get; } = new();

    /// <summary>Appends a child, re-dividing the space equally between everything in the split.</summary>
    public void Add(DockNode child)
    {
        child.Parent = this;
        Children.Add(child);

        // Ratios already sum to 1, so appending a raw 1 would hand the new child roughly as much
        // space as the whole rest of the split combined. Reset everyone to an equal weight first.
        for (int i = 0; i < Ratios.Count; i++) Ratios[i] = 1;
        Ratios.Add(1);
        NormalizeRatios();
    }

    /// <summary>
    /// Scales the ratios back to a sum of 1. Star sizing only cares about the proportions, so
    /// this is housekeeping - it keeps the numbers readable and stops repeated drags from
    /// drifting into very large or very small values.
    /// </summary>
    public void NormalizeRatios()
    {
        while (Ratios.Count < Children.Count) Ratios.Add(1);
        while (Ratios.Count > Children.Count) Ratios.RemoveAt(Ratios.Count - 1);

        double sum = Ratios.Sum();
        if (sum <= 0)
        {
            for (int i = 0; i < Ratios.Count; i++) Ratios[i] = 1.0 / Ratios.Count;
            return;
        }
        for (int i = 0; i < Ratios.Count; i++) Ratios[i] /= sum;
    }
}

/// <summary>
/// A pane: one or more windows stacked in the same rectangle, of which <see cref="Active"/> is
/// the one on show. A pane with several tabs is how the full-view preset holds every window at
/// once, and how a drop onto a pane's centre will merge two windows.
/// </summary>
internal sealed class DockLeafNode : DockNode
{
    public List<IMdiLayoutItem> Tabs { get; } = new();

    /// <summary>The tab this pane is showing, or null while the pane is empty.</summary>
    public IMdiLayoutItem? Active { get; set; }

    /// <summary>
    /// Shows every tab at once, overlapped and stepped down-right, with <see cref="Active"/> on
    /// top - the cascade preset. A plain pane shows only its active tab.
    /// </summary>
    public bool Cascade { get; set; }

    public static DockLeafNode Of(IMdiLayoutItem item)
    {
        var leaf = new DockLeafNode { Active = item };
        leaf.Tabs.Add(item);
        return leaf;
    }
}
