using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Snipyard.Controls;

namespace Snipyard;

/// <summary>What an MDI window holds, for the places that have to tell the kinds apart.</summary>
internal enum MdiItemKind { Terminal, Editor, Graph, Memory, Artifacts }

/// <summary>
/// The part of an MDI window the layout cares about. Terminals, editors and commit graphs each
/// implement it, which is what lets the dock tree, the window strip and the windows panel treat
/// them as one set instead of three parallel lists.
/// </summary>
internal interface IMdiLayoutItem
{
    Border Container { get; }
    Border TitleBar { get; }

    /// <summary>This window's tab in the window strip.</summary>
    Button StripButton { get; }

    /// <summary>The label inside <see cref="StripButton"/>, which a rename writes to.</summary>
    TextBlock StripText { get; }

    MdiItemKind Kind { get; }

    /// <summary>
    /// The tab is the one name a window has, so everything that needs a title reads it from
    /// there rather than keeping a second opinion that a rename would leave behind.
    /// </summary>
    string Title { get; }

    /// <summary>
    /// The window this one is showing in at the moment: the main window, or one it was dragged
    /// out into. Null only while it is in flight between the two. Anything that has to reach a
    /// strip or a dock tree goes through here, because a window is no longer tied to the one it
    /// was opened in.
    /// </summary>
    IDockOwner? Owner { get; set; }
}

/// <summary>
/// A window that can hold MDI windows: the main window, and every window dragged out of it.
/// Tab dragging is written against this rather than against <c>MainWindow</c>, which is what
/// lets a tab be dropped into any of them.
/// </summary>
internal interface IDockOwner
{
    Window Window { get; }

    DockHost Host { get; }

    /// <summary>The tab strip, and the scroller around it that says where the strip's area is.</summary>
    Panel Strip { get; }
    Control StripScroll { get; }

    /// <summary>Full-window layer the drag ghost and the strip caret are drawn on.</summary>
    Canvas Overlay { get; }

    /// <summary>
    /// False for windows this one cannot show. A lightweight window has no toolbar, IME box or
    /// status bar, so a terminal in one would have no way to be typed into.
    /// </summary>
    bool Accepts(IMdiLayoutItem item);

    /// <summary>Takes a window out without closing it, for a drag that landed somewhere else.</summary>
    void Release(IMdiLayoutItem item);

    /// <summary>Takes in a window that came from another one, docking it where the drop says.</summary>
    void Adopt(IMdiLayoutItem item, DockDropTarget target);

    /// <summary>Applies a drop that stayed inside this window.</summary>
    void DockInside(IMdiLayoutItem item, DockDropTarget target);

    /// <summary>Moves a tab to <paramref name="index"/> in this window's strip.</summary>
    void ReorderStrip(IMdiLayoutItem item, int index);

    /// <summary>Brings a window up in its pane and focuses it.</summary>
    void SetActive(IMdiLayoutItem item);

    /// <summary>
    /// Called after a drag has taken a window away. A detached window with nothing left in it
    /// has no reason to stay on screen; the main window ignores this.
    /// </summary>
    void CloseIfEmpty();
}

/// <summary>
/// Every window that can hold MDI windows, most recently activated first - which is the order a
/// drag has to hit-test them in, since they overlap.
/// </summary>
internal static class DockOwners
{
    private static readonly List<IDockOwner> Open = new();

    internal static void Register(IDockOwner owner)
    {
        Raise(owner);
        owner.Window.Activated += (_, _) => Raise(owner);
    }

    internal static void Unregister(IDockOwner owner) => Open.Remove(owner);

    private static void Raise(IDockOwner owner)
    {
        Open.Remove(owner);
        Open.Insert(0, owner);
    }

    /// <summary>
    /// The frontmost window under a point given in screen coordinates, or null when the pointer
    /// is off all of them - which is the signal to give the dragged window one of its own.
    /// </summary>
    internal static IDockOwner? At(PixelPoint screen)
    {
        foreach (var owner in Open)
        {
            var window = owner.Window;
            if (!window.IsVisible) continue;

            var local = window.PointToClient(screen);
            if (new Rect(window.ClientSize).Contains(local)) return owner;
        }
        return null;
    }
}
