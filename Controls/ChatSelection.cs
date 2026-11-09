using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Snipyard.Services;

namespace Snipyard.Controls;

/// <summary>
/// Lets a mouse drag select across the separate text blocks a chat reply is built from, the
/// way a browser selects across paragraphs. Each SelectableTextBlock still handles the press
/// and any selection inside itself; once the drag leaves it for another block, the blocks in
/// between are selected whole and the one under the pointer up to the pointer. Ctrl+C and the
/// context menu then copy the whole span.
/// </summary>
public sealed class ChatSelection
{
    private readonly Control _root;
    private readonly ScrollViewer? _scroller;
    private List<SelectableTextBlock> _blocks = new();
    private SelectableTextBlock? _anchor;
    private int _anchorPos;
    private bool _dragging;
    private bool _spansBlocks;
    private readonly MenuFlyout _menu;
    private string _menuText = "";

    public static ChatSelection Attach(Control root, ScrollViewer? scroller) => new(root, scroller);

    private ChatSelection(Control root, ScrollViewer? scroller)
    {
        _root = root;
        _scroller = scroller;
        root.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        root.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        root.AddHandler(InputElement.PointerReleasedEvent, (_, _) => _dragging = false, RoutingStrategies.Bubble, handledEventsToo: true);
        root.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        root.AddHandler(Control.ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel);
        root.AddHandler(InputElement.LostFocusEvent, OnLostFocus, RoutingStrategies.Bubble);

        var copy = new MenuItem { Header = Loc.Get("Copy") };
        copy.Click += async (_, _) => await SetClipboard(_menuText);
        _menu = new MenuFlyout { Items = { copy } };
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_root).Properties.IsLeftButtonPressed) return;

        // Runs after the block's own handler, so its SelectionStart is already the press point.
        _anchor = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<SelectableTextBlock>().FirstOrDefault();
        _dragging = _anchor != null && e.ClickCount == 1;
        _anchorPos = _anchor?.SelectionStart ?? 0;
        if (_spansBlocks || _anchor == null)
        {
            foreach (var b in _blocks)
                if (b != _anchor) b.ClearSelection();
            _spansBlocks = false;
        }
        if (_dragging)
            _blocks = _root.GetVisualDescendants().OfType<SelectableTextBlock>().Where(b => b.IsEffectivelyVisible).ToList();
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging || _anchor == null) return;
        if (!e.GetCurrentPoint(_root).Properties.IsLeftButtonPressed) { _dragging = false; return; }

        AutoScroll(e);
        var p = e.GetPosition(_root);
        int ai = _blocks.IndexOf(_anchor);
        var (target, ti) = BlockAt(p);
        if (ai < 0 || target == null || ti == ai)
        {
            // Back inside the anchor: it selects within itself, the rest lets go.
            if (_spansBlocks)
            {
                foreach (var b in _blocks)
                    if (b != _anchor) b.ClearSelection();
                _spansBlocks = false;
            }
            return;
        }

        bool forward = ti > ai;
        int lo = Math.Min(ai, ti), hi = Math.Max(ai, ti);
        for (int k = 0; k < _blocks.Count; k++)
        {
            var b = _blocks[k];
            if (k == ai)
            {
                b.SelectAll();
                b.SelectionStart = _anchorPos;
                if (!forward) b.SelectionEnd = 0;
            }
            else if (k == ti)
            {
                int pos = PositionIn(b, p);
                b.SelectAll();
                if (forward) { b.SelectionStart = 0; b.SelectionEnd = pos; }
                else b.SelectionStart = pos;
            }
            else if (k > lo && k < hi) b.SelectAll();
            else b.ClearSelection();
        }
        _spansBlocks = true;
    }

    /// <summary>The block under the pointer, or in a gap the last block that starts above it.</summary>
    private (SelectableTextBlock? Block, int Index) BlockAt(Point p)
    {
        SelectableTextBlock? best = null;
        int bestIndex = -1;
        for (int k = 0; k < _blocks.Count; k++)
        {
            var r = BoundsInRoot(_blocks[k]);
            if (r == null) continue;
            if (r.Value.Contains(p)) return (_blocks[k], k);
            if (r.Value.Top <= p.Y) { best = _blocks[k]; bestIndex = k; }
        }
        return best == null ? (_blocks.FirstOrDefault(), _blocks.Count > 0 ? 0 : -1) : (best, bestIndex);
    }

    private Rect? BoundsInRoot(Visual v)
    {
        var tl = v.TranslatePoint(default, _root);
        return tl == null ? null : new Rect(tl.Value, v.Bounds.Size);
    }

    private int PositionIn(SelectableTextBlock b, Point rootPoint)
    {
        var local = _root.TranslatePoint(rootPoint, b) ?? default;
        if (local.Y >= b.Bounds.Height) return TextLength(b);
        if (local.Y < 0) return 0;
        var hit = b.TextLayout.HitTestPoint(new Point(local.X - b.Padding.Left, local.Y - b.Padding.Top));
        return hit.TextPosition;
    }

    private static int TextLength(SelectableTextBlock b)
    {
        int start = b.SelectionStart, end = b.SelectionEnd;
        b.SelectAll();
        int len = b.SelectionEnd;
        b.SelectionStart = start;
        b.SelectionEnd = end;
        return len;
    }

    /// <summary>Scroll while the drag is held past the top or bottom edge of the view.</summary>
    private void AutoScroll(PointerEventArgs e)
    {
        if (_scroller == null) return;
        double y = e.GetPosition(_scroller).Y, h = _scroller.Bounds.Height;
        double dy = y < 0 ? y : y > h ? y - h : 0;
        if (dy != 0)
            _scroller.Offset = _scroller.Offset.WithY(Math.Clamp(_scroller.Offset.Y + Math.Clamp(dy, -40, 40),
                0, Math.Max(0, _scroller.Extent.Height - _scroller.Viewport.Height)));
    }

    /// <summary>Class on a list's bullet or number, so a copy keeps it on its item's line.</summary>
    public const string ListMarkerClass = "chat-list-marker";

    /// <summary>
    /// The selected blocks' text in order. Blocks side by side (a list marker and its item, the
    /// cells of a table row) stay on one line; each new row of blocks starts a new line.
    /// </summary>
    private string SelectedText()
    {
        var sb = new System.Text.StringBuilder();
        SelectableTextBlock? prev = null;
        foreach (var b in _blocks.Where(b => b.SelectionStart != b.SelectionEnd))
        {
            if (prev != null)
                sb.Append(!SameRow(prev, b) ? Environment.NewLine
                    : prev.Classes.Contains(ListMarkerClass) ? " " : "\t");
            sb.Append(b.SelectedText);
            prev = b;
        }
        return sb.ToString();
    }

    private bool SameRow(Visual a, Visual b)
    {
        var ra = BoundsInRoot(a);
        var rb = BoundsInRoot(b);
        return ra != null && rb != null && rb.Value.Left >= ra.Value.Right - 0.5
            && rb.Value.Top < ra.Value.Bottom && ra.Value.Top < rb.Value.Bottom;
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_spansBlocks || e.Key != Key.C || !Shortcut.HasPrimary(e.KeyModifiers)) return;
        e.Handled = true;
        await SetClipboard(SelectedText());
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (!_spansBlocks || e.Source is not Control source) return;
        e.Handled = true;
        _menuText = SelectedText();
        _menu.ShowAt(source, showAtPointer: true);
    }

    private void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        // The anchor drops its own part when focus leaves it; let the rest go with it.
        if (!_spansBlocks || e.Source != _anchor || _menu.IsOpen) return;
        foreach (var b in _blocks) b.ClearSelection();
        _spansBlocks = false;
    }

    private async System.Threading.Tasks.Task SetClipboard(string text)
    {
        var clipboard = TopLevel.GetTopLevel(_root)?.Clipboard;
        if (clipboard != null && text.Length > 0)
            await clipboard.SetTextAsync(text);
    }
}
