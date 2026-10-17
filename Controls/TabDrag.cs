using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Snipyard.Controls;

/// <summary>
/// Dragging a window by its tab: along the strip to reorder it, onto a pane to dock it there,
/// onto another window to move it across, or clear of every window to give it one of its own.
///
/// There is one drag at a time and one pointer, so the state is held here rather than per window.
/// That is also what lets a window keep working after it moves: the handlers are wired once, when
/// the window is created, and read the window it currently lives in off
/// <see cref="IMdiLayoutItem.Owner"/> instead of capturing the one it was opened in.
/// </summary>
internal static class TabDrag
{
    /// <summary>How far the pointer has to travel before a press on a tab becomes a drag.</summary>
    private const double Threshold = 4;

    private static IMdiLayoutItem? _item;
    private static bool _dragging;
    private static Point _origin;

    /// <summary>The dragged tab drawn under the cursor, and the bar showing where it would land.</summary>
    private static Border? _ghost;
    private static Border? _caret;

    /// <summary>What a release right now would do: a slot in a strip, or a pane in a window.</summary>
    private static IDockOwner? _dropOwner;
    private static int _dropIndex = -1;
    private static DockDropTarget _dropTarget;

    /// <summary>The host currently painting a preview, so it can be cleared without a search.</summary>
    private static DockHost? _previewHost;

    private static PixelPoint _screen;

    /// <summary>
    /// Makes one window draggable, by its strip tab and by its pane header. The header matters
    /// for a window sitting alone in a detached window, where the strip is not shown.
    /// </summary>
    internal static void Hook(IMdiLayoutItem entry)
    {
        Arm(entry, entry.StripButton);
        Arm(entry, entry.TitleBar);
    }

    private static void Arm(IMdiLayoutItem entry, InputElement handle)
    {
        // Tunnel: Button marks PointerPressed and PointerReleased handled in its own class
        // handlers, so an ordinary handler on a tab would never see either of them.
        handle.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
            if (entry.Owner == null) return;

            // Cascaded, the header moves the window around the pane instead; the strip tab
            // still docks it.
            if (ReferenceEquals(handle, entry.TitleBar) && CascadePanel.Moves(entry)) return;

            // The close cross is a button inside the handle; a press that starts there is aimed
            // at it. On a strip tab the handle is itself the button, and that one is the drag.
            if (e.Source is Visual source && source.FindAncestorOfType<Button>(true) is { } inner
                && !ReferenceEquals(inner, handle)) return;

            _item = entry;
            _dragging = false;
            _origin = e.GetPosition(entry.Owner.Overlay);
        }, RoutingStrategies.Tunnel);

        handle.PointerMoved += (_, e) =>
        {
            if (!ReferenceEquals(_item, entry) || entry.Owner is not { } owner) return;

            var local = e.GetPosition(owner.Overlay);
            if (!_dragging)
            {
                if (Math.Abs(local.X - _origin.X) < Threshold &&
                    Math.Abs(local.Y - _origin.Y) < Threshold) return;

                Begin(entry, owner);

                // Without capture the drag would stop the moment the pointer left the handle,
                // which is where every drop target is. Re-capturing to the element it is already
                // on would raise PointerCaptureLost and cancel the drag before it started.
                if (!ReferenceEquals(e.Pointer.Captured, handle)) e.Pointer.Capture(handle);
            }

            _screen = owner.Window.PointToScreen(e.GetPosition(owner.Window));
            Update(owner, local);
        };

        // The release is only observed, never handled: letting the Click through afterwards is
        // what keeps a plain click on a tab - and the double tap that renames it - working.
        handle.AddHandler(InputElement.PointerReleasedEvent, (_, _) => Complete(),
            RoutingStrategies.Tunnel);

        handle.PointerCaptureLost += (_, _) => Cancel();
    }

    private static void Begin(IMdiLayoutItem entry, IDockOwner owner)
    {
        _dragging = true;
        entry.StripButton.Opacity = 0.4;

        _ghost = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(230, 45, 45, 48)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(200, 90, 165, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 4),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = entry.Title,
                FontSize = 11,
                Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 160
            }
        };
        owner.Overlay.Children.Add(_ghost);

        _caret = new Border
        {
            Width = 2,
            Background = new SolidColorBrush(Color.FromArgb(230, 90, 165, 255)),
            IsHitTestVisible = false,
            IsVisible = false
        };
        owner.Overlay.Children.Add(_caret);
    }

    /// <summary>
    /// Moves the ghost to the cursor and shows what a release here would do: a caret between two
    /// tabs while over a strip, the pane preview while over a dock area, and nothing at all off
    /// every window, which is the drop that opens a new one.
    /// </summary>
    private static void Update(IDockOwner source, Point local)
    {
        ClearPreview();
        _dropOwner = null;
        _dropIndex = -1;
        _dropTarget = default;

        var over = DockOwners.At(_screen);
        if (over == null || _item == null || !over.Accepts(_item))
        {
            // Off every window, or over one that will not take this: the ghost stays where the
            // drag started, running off the edge, which reads as the window leaving.
            PlaceGhost(source, local);
            HideCaret();
            return;
        }

        var point = over.Window.PointToClient(_screen);
        PlaceGhost(over, over.Window.TranslatePoint(point, over.Overlay) ?? point);

        var stripOrigin = over.StripScroll.TranslatePoint(new Point(0, 0), over.Window);
        // Effectively: a detached window holding one tab hides the whole strip bar, and the
        // scroller inside it still calls itself visible.
        if (over.StripScroll.IsEffectivelyVisible && stripOrigin != null &&
            new Rect(stripOrigin.Value, over.StripScroll.Bounds.Size).Contains(point))
        {
            _dropOwner = over;
            _dropIndex = StripInsertIndex(over, point);
            ShowCaret(over, _dropIndex);
            return;
        }

        HideCaret();

        var host = over.Window.TranslatePoint(point, over.Host);
        if (host == null) return;

        _dropOwner = over;

        // Only the window the tab already lives in has a "would this move anything" answer to
        // give; coming from elsewhere, every pane is a real destination.
        _dropTarget = over.Host.HitTestDropTarget(
            host.Value, ReferenceEquals(over, source) ? _item : null);

        if (_dropTarget.Kind == DockDropKind.None && !ReferenceEquals(over, source))
            over.Host.ShowAdoptPreview();
        else over.Host.ShowDropPreview(_dropTarget);

        _previewHost = over.Host;
    }

    /// <summary>Draws the ghost on the window the cursor is over, which is not always its own.</summary>
    private static void PlaceGhost(IDockOwner owner, Point at)
    {
        if (_ghost == null) return;

        if (!ReferenceEquals(_ghost.Parent, owner.Overlay))
        {
            (_ghost.Parent as Canvas)?.Children.Remove(_ghost);
            owner.Overlay.Children.Add(_ghost);
        }

        Canvas.SetLeft(_ghost, at.X + 12);
        Canvas.SetTop(_ghost, at.Y + 12);
    }

    /// <summary>Which slot in a strip the cursor is pointing between.</summary>
    private static int StripInsertIndex(IDockOwner owner, Point point)
    {
        int index = 0;
        foreach (var child in owner.Strip.Children)
        {
            var origin = child.TranslatePoint(new Point(0, 0), owner.Window);
            if (origin == null) continue;
            if (point.X < origin.Value.X + child.Bounds.Width / 2) break;
            index++;
        }
        return index;
    }

    private static void ShowCaret(IDockOwner owner, int index)
    {
        if (_caret == null) return;

        // The caret has to be drawn on the window the cursor is over, which is not always the
        // one the drag started in.
        if (!ReferenceEquals(_caret.Parent, owner.Overlay))
        {
            (_caret.Parent as Canvas)?.Children.Remove(_caret);
            owner.Overlay.Children.Add(_caret);
        }

        var origin = owner.Strip.TranslatePoint(new Point(0, 0), owner.Overlay);
        if (origin == null) { _caret.IsVisible = false; return; }

        double x = origin.Value.X;
        var tabs = owner.Strip.Children;
        if (tabs.Count > 0)
        {
            bool past = index >= tabs.Count;
            var neighbour = tabs[past ? tabs.Count - 1 : index];
            var at = neighbour.TranslatePoint(new Point(0, 0), owner.Overlay);
            if (at != null) x = past ? at.Value.X + neighbour.Bounds.Width + 1 : at.Value.X - 3;
        }

        Canvas.SetLeft(_caret, x);
        Canvas.SetTop(_caret, origin.Value.Y);
        _caret.Height = Math.Max(owner.Strip.Bounds.Height, 18);
        _caret.IsVisible = true;
    }

    private static void HideCaret()
    {
        if (_caret != null) _caret.IsVisible = false;
    }

    private static void ClearPreview()
    {
        _previewHost?.ClearDropPreview();
        _previewHost = null;
    }

    /// <summary>Applies whatever the last <see cref="Update"/> was previewing.</summary>
    private static void Complete()
    {
        var item = _item;
        var source = item?.Owner;
        bool dragged = _dragging;
        var over = _dropOwner;
        int index = _dropIndex;
        var target = _dropTarget;
        var screen = _screen;

        Cancel();
        if (!dragged || item == null || source == null) return;

        if (over == null) { Detach(item, source, screen); return; }

        if (index >= 0)
        {
            if (ReferenceEquals(over, source)) source.ReorderStrip(item, index);
            else { Move(item, source, over, default); over.ReorderStrip(item, index); }
            return;
        }

        // No pane answered: the drop was on an empty dock area, or on the window around it.
        // Landing on another window still means "show it there", where it stacks with the rest.
        if (target.Kind == DockDropKind.None)
        {
            if (!ReferenceEquals(over, source)) Move(item, source, over, default);
            return;
        }

        if (ReferenceEquals(over, source)) source.DockInside(item, target);
        else Move(item, source, over, target);
    }

    private static void Move(IMdiLayoutItem item, IDockOwner from, IDockOwner to, DockDropTarget target)
    {
        from.Release(item);
        to.Adopt(item, target);
        to.Window.Activate();
        from.CloseIfEmpty();
    }

    /// <summary>Gives a window dropped clear of everything a window of its own.</summary>
    private static void Detach(IMdiLayoutItem item, IDockOwner from, PixelPoint screen)
    {
        // A window already alone in a window of its own is as detached as it gets; pulling it
        // out would close one window and open an identical one in its place.
        if (from is DetachedWindow lone && lone.Count <= 1) return;
        if (from is AppShell shell && shell.IsLoneSecondary) return;

        // A terminal is worked through the toolbar, the IME box and the status bar around it, so
        // it takes a whole shell with it. Everything else only needs somewhere to be drawn.
        if (item.Kind == MdiItemKind.Terminal) Into(new ShellWindow(), item, from, screen);
        else Into(new DetachedWindow(), item, from, screen);
    }

    /// <summary>Opens <paramref name="window"/> under the cursor and moves the window into it.</summary>
    private static void Into(Window window, IMdiLayoutItem item, IDockOwner from, PixelPoint screen)
    {
        // Under the cursor rather than at it: dropping the window exactly on the pointer puts
        // its title bar where the tab was being held, which reads as the tab having vanished.
        window.Position = new PixelPoint(screen.X - 80, screen.Y - 16);

        // Out of the old window before the new one opens, and into it only on a later pass. A
        // control that changes window inside one layout pass leaves the window it left holding
        // an invalidation for a control it no longer owns, which that window throws on.
        from.Release(item);

        // Shown before anything moves into it: a shell finds the window it is in, and registers
        // itself as a drop target, only once it is actually in one.
        window.Show();

        Dispatcher.UIThread.Post(() =>
        {
            Owner(window).Adopt(item, default);
            from.CloseIfEmpty();
        }, DispatcherPriority.Background);
    }

    /// <summary>What in <paramref name="window"/> takes windows in - the window, or its shell.</summary>
    private static IDockOwner Owner(Window window) =>
        window is ShellWindow shell ? shell.Shell : (IDockOwner)window;

    /// <summary>Puts a drag back the way it was found. Safe to call when no drag is running.</summary>
    private static void Cancel()
    {
        if (_item != null) _item.StripButton.Opacity = 1;
        _item = null;
        _dragging = false;
        _dropOwner = null;
        _dropIndex = -1;
        _dropTarget = default;

        if (_ghost != null) { (_ghost.Parent as Canvas)?.Children.Remove(_ghost); _ghost = null; }
        if (_caret != null) { (_caret.Parent as Canvas)?.Children.Remove(_caret); _caret = null; }
        ClearPreview();
    }
}
