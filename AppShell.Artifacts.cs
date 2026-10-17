using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Snipyard.Controls;
using Snipyard.Services;

namespace Snipyard;

/// <summary>
/// The Artifacts button's window: the artifacts the working folder's sessions have published,
/// opened as an MDI window beside the terminal. One window per folder; asking again for a folder
/// that has one brings it forward and rereads the list.
/// </summary>
internal partial class AppShell
{
    private sealed class ArtifactsChildInfo : IMdiLayoutItem
    {
        public required Border Container { get; init; }
        public required Border TitleBar { get; init; }
        public required TextBlock TitleText { get; init; }
        public required Controls.ArtifactsView View { get; init; }
        public required Button StripButton { get; init; }
        public required TextBlock StripText { get; init; }

        public MdiItemKind Kind => MdiItemKind.Artifacts;
        public string Title => StripText.Text ?? string.Empty;
        public IDockOwner? Owner { get; set; }
    }

    private readonly List<ArtifactsChildInfo> _artifactsChildren = new();

    /// <summary>Claude's clay: the colour of the artifact pane it stands in for.</summary>
    private static readonly Color ArtifactsDotColor = Color.FromRgb(217, 119, 87);

    /// <summary>The activity bar's artifacts glyph, reused on the tab. Kept in step with <c>BtnActivityArtifacts</c>.</summary>
    private static readonly Geometry ArtifactsTabIcon = Geometry.Parse(
        "M11 3A4.5 4.5 0 1 1 11 12A4.5 4.5 0 1 1 11 3ZM11 5A2.5 2.5 0 1 0 11 10A2.5 2.5 0 1 0 11 5ZM6 14A3.5 3.5 0 1 1 6 21A3.5 3.5 0 1 1 6 14ZM6 16A1.5 1.5 0 1 0 6 19A1.5 1.5 0 1 0 6 16ZM14.5 13H19.5A1.5 1.5 0 0 1 21 14.5V19.5A1.5 1.5 0 0 1 19.5 21H14.5A1.5 1.5 0 0 1 13 19.5V14.5A1.5 1.5 0 0 1 14.5 13ZM15 15V19H19V15Z");

    private void OnActivityArtifacts(object? sender, RoutedEventArgs e) => OpenArtifactsWindow();

    private void OpenArtifactsWindow()
    {
        var folder = _projectFolder;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

        var existing = _artifactsChildren.FirstOrDefault(a =>
            string.Equals(a.View.ProjectFolder, folder, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            ActivateLayoutItem(existing);
            _ = existing.View.LoadAsync();
            return;
        }

        var view = new Controls.ArtifactsView(folder, _isDark);
        var title = string.Format(Loc.Get("ArtifactsWindowTitle"), Path.GetFileName(folder.TrimEnd('\\', '/')));

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 13,
            Foreground = new SolidColorBrush(MdiTitleFg(_isDark)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        ToolTip.SetTip(titleText, folder);

        var closeButton = new Button
        {
            Content = "×",
            FontSize = 14,
            Padding = new Thickness(6, 0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(MdiTitleButtonFg(_isDark)),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        var titleGrid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        titleText.Margin = new Thickness(8, 0);
        Grid.SetColumn(titleText, 0);
        Grid.SetColumn(closeButton, 1);
        titleGrid.Children.Add(titleText);
        titleGrid.Children.Add(closeButton);

        var titleBar = new Border
        {
            Background = new SolidColorBrush(MdiTitleBarBg(_isDark)),
            Padding = new Thickness(0, 6),
            Child = titleGrid,
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        var dockPanel = new DockPanel();
        DockPanel.SetDock(titleBar, Dock.Top);
        dockPanel.Children.Add(titleBar);
        dockPanel.Children.Add(view);

        var container = new Border
        {
            Child = dockPanel,
            BorderBrush = new SolidColorBrush(MdiContainerBorder(_isDark)),
            BorderThickness = new Thickness(0.5),
            ClipToBounds = true,
            Background = new SolidColorBrush(MdiContainerBg(_isDark))
        };

        var (stripButton, _, stripText, _, stripCloseBtn) =
            BuildStripButton(title, ArtifactsDotColor, _isDark, ArtifactsTabIcon);
        ToolTip.SetTip(stripButton, folder);

        var entry = new ArtifactsChildInfo
        {
            Container = container,
            TitleBar = titleBar,
            TitleText = titleText,
            View = view,
            StripButton = stripButton,
            StripText = stripText
        };

        stripButton.Click += (_, _) => ActivateLayoutItem(entry);
        stripCloseBtn.Click += (_, e) => { CloseArtifactsWindow(entry); e.Handled = true; };
        closeButton.Click += (_, _) => CloseArtifactsWindow(entry);

        // The list rows mark PointerPressed handled, so selecting the window has to happen on the
        // way down
        container.AddHandler(InputElement.PointerPressedEvent,
            (_, _) => SetActiveLayoutItem(entry), RoutingStrategies.Tunnel);

        _artifactsChildren.Add(entry);
        entry.Owner = this;
        TabDrag.Hook(entry);
        WindowStrip.Children.Add(stripButton);
        _activeLayoutItem = entry;
        ArrangeChildren();
        Dispatcher.UIThread.Post(() => view.Focus());
    }

    private void CloseArtifactsWindow(ArtifactsChildInfo entry)
    {
        if (OwnerOf(entry) is { } holder && !ReferenceEquals(holder, this))
        {
            holder.CloseArtifactsWindow(entry);
            return;
        }

        _artifactsChildren.Remove(entry);
        entry.Owner?.Release(entry);
    }

    /// <summary>An artifacts window, listed the same way an open file is.</summary>
    private Control BuildArtifactsWindowRow(ArtifactsChildInfo artifacts) =>
        BuildLayoutWindowRow(artifacts, artifacts.View.ProjectFolder, ArtifactsDotColor);
}
