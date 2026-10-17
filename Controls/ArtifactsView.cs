using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Snipyard.Services;

namespace Snipyard.Controls;

/// <summary>
/// The artifacts a project's sessions have published, as a list beside a WebView2 showing the one
/// picked - the Claude Desktop artifact pane, for one working folder. A page is shown from the
/// local file the publish sent while that file is still there, since the claude.ai copy is private
/// and needs a login inside the embedded browser; the toolbar switches to the online copy.
/// </summary>
public sealed class ArtifactsView : UserControl
{
    private readonly string _projectFolder;
    private readonly StackPanel _list = new() { Spacing = 2, Margin = new Thickness(6) };
    private readonly TextBlock _listHeader = new() { FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _refresh;
    private readonly TextBlock _pageTitle = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _source;
    private readonly Button _reload;
    private readonly Button _external;
    private readonly Panel _body = new();
    private readonly TextBlock _message;
    private readonly Border _listPane;
    private readonly Border _toolbar;
    private WebViewHost? _host;
    private List<ArtifactEntry> _entries = new();
    private ArtifactEntry? _selected;
    private bool _showLocal;
    private bool _isDark;

    public ArtifactsView(string projectFolder, bool isDark)
    {
        _projectFolder = projectFolder;
        _isDark = isDark;

        _refresh = ToolButton("⟳", () => _ = LoadAsync());
        ToolTip.SetTip(_refresh, Loc.Get("ArtifactsRefresh"));
        var listTop = new DockPanel { Margin = new Thickness(10, 6, 6, 6) };
        DockPanel.SetDock(_refresh, Dock.Right);
        listTop.Children.Add(_refresh);
        listTop.Children.Add(_listHeader);
        var listDock = new DockPanel();
        DockPanel.SetDock(listTop, Dock.Top);
        listDock.Children.Add(listTop);
        listDock.Children.Add(new ScrollViewer { Content = _list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        _listPane = new Border { Child = listDock, BorderThickness = new Thickness(0, 0, 0.5, 0) };

        _source = ToolButton("", () => { _showLocal = !_showLocal; ShowSelected(); });
        _reload = ToolButton("⟳", () => _host?.Reload());
        ToolTip.SetTip(_reload, Loc.Get("PreviewReload"));
        _external = ToolButton("↗", () => { if (_selected != null) OpenExternally(_selected.Url); });
        ToolTip.SetTip(_external, Loc.Get("PreviewOpenExternal"));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        buttons.Children.Add(_source);
        buttons.Children.Add(_reload);
        buttons.Children.Add(_external);
        var toolbarRow = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        toolbarRow.Children.Add(buttons);
        toolbarRow.Children.Add(_pageTitle);
        _toolbar = new Border { Padding = new Thickness(10, 6), Child = toolbarRow, BorderThickness = new Thickness(0, 0, 0, 0.5) };

        _message = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(20),
        };
        _body.Children.Add(_message);

        var right = new DockPanel();
        DockPanel.SetDock(_toolbar, Dock.Top);
        right.Children.Add(_toolbar);
        right.Children.Add(_body);

        var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("260,4,*") };
        Grid.SetColumn(_listPane, 0);
        var splitter = new GridSplitter { Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(_listPane);
        grid.Children.Add(splitter);
        grid.Children.Add(right);
        Content = grid;

        UpdateToolbar();
        ApplyTheme(isDark);
        _ = LoadAsync();
    }

    public string ProjectFolder => _projectFolder;

    /// <summary>Rereads the transcripts, keeping the current pick when it is still listed.</summary>
    public async Task LoadAsync()
    {
        _listHeader.Text = Loc.Get("ArtifactsLoading");
        _refresh.IsEnabled = false;
        var folder = _projectFolder;
        var entries = await Task.Run(() => ArtifactCatalog.Load(folder));
        _refresh.IsEnabled = true;
        _entries = entries;
        _listHeader.Text = string.Format(Loc.Get("ArtifactsCount"), entries.Count);

        var keep = _selected == null ? null : entries.Find(e => e.Id == _selected.Id);
        RebuildList();
        if (keep != null) Select(keep, reload: keep.Url != _selected?.Url);
        else if (entries.Count > 0) Select(entries[0], reload: true);
        else
        {
            _selected = null;
            ShowMessage(Loc.Get("ArtifactsEmpty"));
            UpdateToolbar();
        }
    }

    private void RebuildList()
    {
        _list.Children.Clear();
        foreach (var entry in _entries) _list.Children.Add(BuildRow(entry));
    }

    private Control BuildRow(ArtifactEntry entry)
    {
        var title = new TextBlock { Text = entry.Title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = new SolidColorBrush(MemoryColors.Text(_isDark)) };
        var meta = new TextBlock
        {
            Text = entry.Publishes > 1
                ? $"{entry.Updated:yyyy-MM-dd HH:mm} · {string.Format(Loc.Get("ArtifactsVersions"), entry.Publishes)}"
                : $"{entry.Updated:yyyy-MM-dd HH:mm}",
            FontSize = 11,
            Foreground = new SolidColorBrush(MemoryColors.DimText(_isDark)),
        };
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(title);
        stack.Children.Add(meta);
        if (!string.IsNullOrWhiteSpace(entry.Description))
            stack.Children.Add(new TextBlock
            {
                Text = entry.Description,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(MemoryColors.DimText(_isDark)),
            });

        var row = new Border
        {
            Child = stack,
            Padding = new Thickness(8, 6),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = entry,
        };
        ToolTip.SetTip(row, entry.Url);
        row.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
            Select(entry, reload: true);
            e.Handled = true;
        };
        // The source file usually sits in a session scratchpad that gets cleared, so whether it can
        // be shown is decided when the menu opens rather than when the list was read
        var reveal = new MenuItem { Header = Loc.Get("ShowInExplorer") };
        reveal.Click += (_, _) => RevealInExplorer(entry);
        var menu = new ContextMenu { ItemsSource = new[] { reveal } };
        menu.Opening += (_, _) =>
        {
            var exists = HasLocalFile(entry);
            reveal.IsEnabled = exists;
            ToolTip.SetTip(reveal, exists ? entry.LocalPath : Loc.Get("ArtifactsLocalMissing"));
        };
        row.ContextMenu = menu;
        row.PointerEntered += (_, _) => { if (_selected != entry) row.Background = new SolidColorBrush(ChatTheme.Hover(_isDark)); };
        row.PointerExited += (_, _) => { if (_selected != entry) row.Background = Brushes.Transparent; };
        return row;
    }

    private void Select(ArtifactEntry entry, bool reload)
    {
        _selected = entry;
        foreach (var child in _list.Children)
            if (child is Border b)
                b.Background = b.Tag == entry ? new SolidColorBrush(MemoryColors.SelectionBg(_isDark)) : Brushes.Transparent;
        if (!reload) return;
        _showLocal = HasLocalFile(entry);
        ShowSelected();
    }

    private void ShowSelected()
    {
        UpdateToolbar();
        if (_selected == null) return;

        string target;
        if (_showLocal && HasLocalFile(_selected)) target = new Uri(_selected.LocalPath!).AbsoluteUri;
        else
        {
            _showLocal = false;
            target = _selected.Url;
        }

        if (!WebViewHost.RuntimeAvailable(out _))
        {
            ShowMessage(Loc.Get("PreviewNoRuntime"));
            return;
        }
        if (_host == null)
        {
            _host = new WebViewHost();
            _host.Failed += err => { ShowMessage(err); };
            _body.Children.Add(_host);
        }
        _message.IsVisible = false;
        _host.IsVisible = true;
        _host.Navigate(target);
        UpdateToolbar();
    }

    private void ShowMessage(string text)
    {
        _message.Text = text;
        _message.IsVisible = true;
        if (_host != null)
        {
            _body.Children.Remove(_host);
            _host = null;
        }
    }

    private void UpdateToolbar()
    {
        _pageTitle.Text = _selected?.Title ?? "";
        ToolTip.SetTip(_pageTitle, _selected == null ? null : _showLocal ? _selected.LocalPath : _selected.Url);
        var hasEntry = _selected != null;
        _source.IsVisible = hasEntry;
        _source.IsEnabled = hasEntry && HasLocalFile(_selected!);
        _source.Content = _showLocal ? Loc.Get("ArtifactsShowOnline") : Loc.Get("ArtifactsShowLocal");
        ToolTip.SetTip(_source, hasEntry && !HasLocalFile(_selected!) ? Loc.Get("ArtifactsLocalMissing") : null);
        _reload.IsEnabled = hasEntry;
        _external.IsEnabled = hasEntry;
    }

    private static bool HasLocalFile(ArtifactEntry entry)
    {
        try { return entry.LocalPath != null && File.Exists(entry.LocalPath); }
        catch { return false; }
    }

    public void ApplyTheme(bool isDark)
    {
        _isDark = isDark;
        var fg = new SolidColorBrush(MemoryColors.Text(isDark));
        var outline = new SolidColorBrush(ChatTheme.Outline(isDark));
        Background = new SolidColorBrush(ChatTheme.Background(isDark));
        _listPane.Background = new SolidColorBrush(ChatTheme.Surface(isDark));
        _listPane.BorderBrush = outline;
        _toolbar.Background = new SolidColorBrush(ChatTheme.Surface(isDark));
        _toolbar.BorderBrush = outline;
        _listHeader.Foreground = new SolidColorBrush(MemoryColors.DimText(isDark));
        _pageTitle.Foreground = fg;
        _message.Foreground = new SolidColorBrush(MemoryColors.DimText(isDark));
        foreach (var b in new[] { _refresh, _source, _reload, _external })
        {
            b.Foreground = fg;
            b.BorderBrush = outline;
        }
        RebuildList();
        if (_selected != null) Select(_selected, reload: false);
    }

    private Button ToolButton(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text,
            Padding = new Thickness(8, 2),
            FontSize = 12,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static void RevealInExplorer(ArtifactEntry entry)
    {
        if (!HasLocalFile(entry)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{entry.LocalPath}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private static void OpenExternally(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }
}
