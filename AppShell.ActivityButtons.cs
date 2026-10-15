using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Snipyard.Services;

namespace Snipyard;

/// <summary>
/// The user's prompt buttons at the foot of the activity bar: a click sends the prompt to the
/// active session, a right-click customizes or deletes the button, and a click on the bar's
/// empty stretch adds a new one.
/// </summary>
internal partial class AppShell
{
    private List<ActivityButtonDef> ActivityButtons =>
        _settings.ActivityButtons ??= ActivityButtonDef.CreateDefaults();

    /// <summary>
    /// Rebuilds the buttons from settings. Runs from ApplyProviderUi, so it follows both an AI
    /// switch (the compact button needs a CLI that has /compact) and a language change.
    /// </summary>
    private void RebuildCustomActivityButtons()
    {
        CustomActivityButtons.Children.Clear();
        foreach (var def in ActivityButtons)
        {
            var icon = ActivityIcons.Get(def.IconKey);
            var btn = new Button
            {
                Classes = { "activity-btn" },
                Content = new PathIcon { Data = Geometry.Parse(icon.Data), Width = icon.Size, Height = icon.Size },
                IsVisible = def.Id != ActivityButtonDef.CompactId || _cli.Features.CompactButton,
            };
            ToolTip.SetTip(btn, ActivityButtonLabel(def));
            btn.Click += (_, _) => SendSnippetToActive(def.Prompt);

            var customize = new MenuItem { Header = Loc.Get("ActivityBtnCustomize") };
            customize.Click += (_, _) => _ = CustomizeActivityButton(def);
            var delete = new MenuItem { Header = Loc.Get("ActivityBtnDelete") };
            delete.Click += (_, _) => _ = DeleteActivityButton(def);
            btn.ContextMenu = new ContextMenu { Items = { customize, delete } };

            CustomActivityButtons.Children.Add(btn);
        }

        var add = new MenuItem { Header = Loc.Get("ActivityBtnAdd") };
        add.Click += (_, _) => _ = AddActivityButton();
        _activityAddMenu = new ContextMenu { Items = { add } };
    }

    private ContextMenu? _activityAddMenu;

    /// <summary>
    /// A left or right click anywhere on the bar that misses a button - the stretch between the
    /// two groups, which is only a few pixels on a short window, or the gaps around buttons -
    /// offers to add a button.
    /// </summary>
    private void OnActivityBarBlankPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) != null) return;
        var props = e.GetCurrentPoint(ActivityBarPanel).Properties;
        if (!props.IsLeftButtonPressed && !props.IsRightButtonPressed) return;
        _activityAddMenu?.Open(ActivityBarPanel);
        e.Handled = true;
    }

    /// <summary>
    /// What the button is called: its own tooltip, else the stock compact wording for the
    /// untouched compact button, else the prompt's first line.
    /// </summary>
    private static string ActivityButtonLabel(ActivityButtonDef def)
    {
        if (!string.IsNullOrWhiteSpace(def.Tooltip)) return def.Tooltip.Trim();
        if (def.Id == ActivityButtonDef.CompactId && def.Prompt == ActivityButtonDef.CompactPrompt)
            return Loc.Get("CompactTooltip");
        if (def.Id == ActivityButtonDef.ClearId && def.Prompt == ActivityButtonDef.ClearPrompt)
            return Loc.Get("ClearTooltip");
        return FirstLine(def.Prompt);
    }

    private async Task AddActivityButton()
    {
        var def = await ShowActivityButtonDialog(null);
        if (def == null) return;
        // On top of the bottom group, so the new button lands nearest the spot that was clicked
        ActivityButtons.Insert(0, def);
        _settings.Save();
        RebuildCustomActivityButtons();
    }

    private async Task CustomizeActivityButton(ActivityButtonDef existing)
    {
        var def = await ShowActivityButtonDialog(existing);
        if (def == null) return;
        int index = ActivityButtons.IndexOf(existing);
        if (index < 0) return;
        ActivityButtons[index] = def;
        _settings.Save();
        RebuildCustomActivityButtons();
    }

    private async Task DeleteActivityButton(ActivityButtonDef def)
    {
        var message = string.Format(Loc.Get("ActivityBtnDeleteConfirm"), ActivityButtonLabel(def));
        if (!await ShowConfirmDialog(Loc.Get("ActivityBtnDeleteTitle"), message)) return;
        ActivityButtons.Remove(def);
        _settings.Save();
        RebuildCustomActivityButtons();
    }

    /// <summary>
    /// Icon picker plus prompt box. Returns the new definition, or null when cancelled.
    /// Editing keeps the button's id, so the compact button stays tied to the CLI's /compact
    /// support even after its icon or prompt changes.
    /// </summary>
    private Task<ActivityButtonDef?> ShowActivityButtonDialog(ActivityButtonDef? existing)
    {
        var source = new TaskCompletionSource<ActivityButtonDef?>();
        string selectedKey = existing != null ? ActivityIcons.Get(existing.IconKey).Key : ActivityIcons.DefaultForNew.Key;

        TextBlock Caption(string key) => new()
        {
            Text = Loc.Get(key),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(DialogSubtle()),
        };

        var iconRow = new WrapPanel { Orientation = Orientation.Horizontal };
        var toggles = new List<ToggleButton>();
        foreach (var icon in ActivityIcons.All)
        {
            var toggle = new ToggleButton
            {
                Width = 48,
                Height = 42,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                IsChecked = icon.Key == selectedKey,
                Content = new PathIcon { Data = Geometry.Parse(icon.Data), Width = icon.Size, Height = icon.Size },
            };
            toggle.Click += (_, _) =>
            {
                selectedKey = icon.Key;
                foreach (var other in toggles) other.IsChecked = other == toggle;
            };
            toggles.Add(toggle);
            iconRow.Children.Add(toggle);
        }

        var box = new TextBox
        {
            Text = existing?.Prompt ?? "",
            PlaceholderText = Loc.Get("ActivityBtnPromptHint"),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Padding = new Thickness(8, 6),
            Height = 110,
            // The theme's selection is a dark grey that swallows the dark text; tint it blue
            // and keep the text colour, as the tab rename box does
            SelectionBrush = new SolidColorBrush(_isDark ? Color.FromArgb(90, 50, 120, 220) : Color.FromArgb(90, 0, 122, 255)),
            SelectionForegroundBrush = new SolidColorBrush(DialogForeground()),
        };

        var tipBox = new TextBox
        {
            Text = existing?.Tooltip ?? "",
            PlaceholderText = Loc.Get("ActivityBtnTooltipHint"),
            FontSize = 13,
            Padding = new Thickness(8, 6),
            SelectionBrush = box.SelectionBrush,
            SelectionForegroundBrush = box.SelectionForegroundBrush,
        };

        var ok = new Button
        {
            Content = Loc.Get(existing == null ? "ActivityBtnAddButton" : "Save"),
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsEnabled = !string.IsNullOrWhiteSpace(box.Text),
        };
        var cancel = new Button
        {
            Content = Loc.Get("Cancel"),
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        box.TextChanged += (_, _) => ok.IsEnabled = !string.IsNullOrWhiteSpace(box.Text);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(22, 20) };
        panel.Children.Add(Caption("ActivityBtnIcon"));
        panel.Children.Add(iconRow);
        panel.Children.Add(Caption("ActivityBtnPrompt"));
        panel.Children.Add(box);
        panel.Children.Add(Caption("ActivityBtnTooltip"));
        panel.Children.Add(tipBox);
        buttons.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(buttons);

        var dialog = CreateToolDialog(Loc.Get(existing == null ? "ActivityBtnAddTitle" : "ActivityBtnEditTitle"), 440, 0);
        dialog.SizeToContent = SizeToContent.Height;
        dialog.Content = panel;

        bool answered = false;
        void Finish(ActivityButtonDef? result)
        {
            answered = true;
            source.TrySetResult(result);
            dialog.Close();
        }
        void Accept()
        {
            var prompt = (box.Text ?? "").Replace("\r\n", "\n").Trim();
            if (prompt.Length == 0) return;
            Finish(new ActivityButtonDef
            {
                Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
                IconKey = selectedKey,
                Prompt = prompt,
                Tooltip = (tipBox.Text ?? "").Trim(),
            });
        }

        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => Finish(null);
        // Enter is a line break in the prompt; Ctrl+Enter accepts, Esc cancels. Tunnel, because
        // the TextBox takes Enter for itself on the way back up.
        dialog.AddHandler(InputElement.KeyDownEvent, (_, args) =>
        {
            if (args.Key == Key.Escape) { Finish(null); args.Handled = true; }
            else if (args.Key == Key.Enter && args.KeyModifiers.HasFlag(KeyModifiers.Control)) { Accept(); args.Handled = true; }
        }, RoutingStrategies.Tunnel);
        dialog.Closed += (_, _) => { if (!answered) source.TrySetResult(null); };

        _ = dialog.ShowDialog(HostWindow);
        Dispatcher.UIThread.Post(() => { box.Focus(); box.SelectAll(); });
        return source.Task;
    }
}
