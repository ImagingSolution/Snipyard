using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Avalonia;
using Snipyard.Controls;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;

namespace Snipyard.Services;

/// <summary>
/// Turns the links in a note from inert text into something the reader can follow: both
/// <c>[[wikilink]]</c> and the ordinary <c>[title](slug.md)</c> form that MEMORY.md writes its index
/// with. Passed only by callers that render a linked set of notes; everything else leaves it null
/// and gets the old behaviour, where a link is coloured text and nothing more.
/// </summary>
/// <param name="Navigate">Called with the link target when the reader clicks it.</param>
/// <param name="Exists">
/// Whether that target resolves to anything. A link that does not is still drawn and still
/// clickable - it is marked in a different colour rather than hidden, because in Claude's memory
/// format a link to a note that does not exist yet is a deliberate note-to-self.
/// </param>
/// <param name="OpenUrl">
/// Called with an <c>http(s)</c> address the reader clicks. A caller with nowhere to send one
/// leaves it null, and those links stay inert.
/// </param>
public sealed record WikiLinkOptions(Action<string> Navigate, Func<string, bool> Exists,
    Action<string>? OpenUrl = null);

/// <summary>
/// Lightweight Markdown to Avalonia controls parser.
/// Handles the subset of Markdown that Claude commonly uses in responses.
/// </summary>
public static class MarkdownParser
{
    /// <summary>
    /// Palette of the Claude desktop app's transcript, used when <c>chatStyle</c> is set: prose in
    /// neutral ink, inline code as a tinted chip in a warm red, hairline-bordered code blocks and
    /// tables. Also read by the chat view so its bubbles and cards match.
    /// </summary>
    public sealed record ChatPalette(Color Fg, Color Dim, Color Border, Color CodeBg, Color InlineCodeFg,
        Color CodeFg, Color StringFg, Color CommentFg, Color TableHeaderBg, Color DiffAddFg, Color DiffDelFg)
    {
        public static ChatPalette For(bool isDark) => isDark
            ? new(Color.FromRgb(240, 239, 236), Color.FromRgb(137, 135, 129), Color.FromRgb(45, 45, 45),
                  Color.FromRgb(26, 26, 25), Color.FromRgb(236, 126, 126), Color.FromRgb(228, 228, 226),
                  Color.FromRgb(152, 205, 130), Color.FromRgb(128, 127, 120), Color.FromRgb(33, 33, 33),
                  Color.FromRgb(126, 204, 120), Color.FromRgb(240, 120, 115))
            : new(Color.FromRgb(11, 11, 11), Color.FromRgb(137, 135, 129), Color.FromRgb(221, 221, 220),
                  Color.FromRgb(255, 255, 255), Color.FromRgb(142, 38, 38), Color.FromRgb(20, 20, 20),
                  Color.FromRgb(28, 128, 58), Color.FromRgb(125, 123, 118), Color.FromRgb(240, 240, 239),
                  Color.FromRgb(30, 130, 40), Color.FromRgb(200, 30, 30));

        /// <summary>Tint behind an inline code chip.</summary>
        public static Color InlineCodeBg(bool isDark) => isDark ? Color.FromRgb(33, 33, 33) : Color.FromRgb(240, 240, 239);
    }

    private static readonly Typeface ChatCodeFont =
        new(new FontFamily("Cascadia Mono, Consolas, BIZ UDGothic, Yu Gothic UI, Meiryo"));

    /// <summary>Background for inline code runs while a chat-style parse is running (UI thread only).</summary>
    private static IBrush? _inlineCodeBg;
    private static IBrush? _inlineCodeBorder;

    public static List<Control> Parse(string markdown, bool isDark, Typeface? codeTypeface = null,
        double baseFontSize = 13, WikiLinkOptions? wiki = null, bool chatStyle = false)
    {
        if (!chatStyle)
            return ParseCore(markdown, isDark, codeTypeface, baseFontSize, wiki, null);

        _inlineCodeBg = new SolidColorBrush(ChatPalette.InlineCodeBg(isDark));
        _inlineCodeBorder = new SolidColorBrush(ChatPalette.For(isDark).Border);
        try
        {
            var controls = ParseCore(markdown, isDark, codeTypeface, baseFontSize, wiki, ChatPalette.For(isDark));
            foreach (var c in controls) ApplyChatSelection(c);
            return controls;
        }
        finally { _inlineCodeBg = null; _inlineCodeBorder = null; }
    }

    public static readonly IBrush ChatSelectionBg = new SolidColorBrush(Color.FromRgb(50, 102, 208));

    /// <summary>
    /// Selection as the desktop app shows it: solid blue with white text. The theme's default
    /// is a dark translucent grey that leaves the text in its own colour, so ink and diff red
    /// read poorly through it.
    /// </summary>
    private static void ApplyChatSelection(ILogical node)
    {
        if (node is SelectableTextBlock stb)
        {
            stb.SelectionBrush = ChatSelectionBg;
            stb.SelectionForegroundBrush = Brushes.White;
        }
        foreach (var child in node.LogicalChildren)
            ApplyChatSelection(child);
    }

    private static List<Control> ParseCore(string markdown, bool isDark, Typeface? codeTypeface,
        double baseFontSize, WikiLinkOptions? wiki, ChatPalette? chat)
    {
        var controls = new List<Control>();
        if (string.IsNullOrEmpty(markdown)) return controls;

        var fg = chat?.Fg ?? (isDark ? Color.FromRgb(220, 220, 225) : Color.FromRgb(28, 28, 30));
        var codeBg = chat?.CodeBg ?? (isDark ? Color.FromRgb(40, 40, 44) : Color.FromRgb(240, 240, 245));
        var codeFg = chat?.InlineCodeFg ?? (isDark ? Color.FromRgb(190, 220, 255) : Color.FromRgb(30, 60, 120));
        var quoteBorder = chat?.Border ?? (isDark ? Color.FromRgb(0, 122, 255) : Color.FromRgb(0, 100, 200));
        var linkColor = isDark ? Color.FromRgb(100, 180, 255) : Color.FromRgb(0, 100, 200);
        // A wikilink pointing at a note that has not been written yet.
        var brokenLinkColor = isDark ? Color.FromRgb(230, 150, 110) : Color.FromRgb(175, 85, 20);
        var headingColor = chat?.Fg ?? (isDark ? Color.FromRgb(240, 240, 245) : Color.FromRgb(20, 20, 24));
        var dimColor = chat?.Dim ?? (isDark ? Color.FromRgb(140, 140, 145) : Color.FromRgb(88, 88, 96));
        var markerColor = chat?.Fg ?? (isDark ? Color.FromRgb(0, 122, 255) : Color.FromRgb(0, 100, 200));
        var ruleColor = chat?.Border ?? (isDark ? Color.FromRgb(60, 60, 65) : Color.FromRgb(200, 200, 205));
        // The chat view passes the terminal's font, which is often a narrow CJK mono; code there
        // reads like the desktop app's instead: Cascadia Mono, whose strokes carry more weight than
        // Consolas', with a Japanese gothic behind it.
        var codeFont = chat != null ? ChatCodeFont
            : codeTypeface ?? new Typeface("Cascadia Mono, Consolas, Courier New");

        var lines = markdown.Split('\n');
        int i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];

            // Empty line → spacer
            if (string.IsNullOrWhiteSpace(line))
            {
                controls.Add(new Border { Height = 6 });
                i++;
                continue;
            }

            // Heading: # ## ### ####
            var headingMatch = Regex.Match(line, @"^(#{1,4})\s+(.+)$");
            if (headingMatch.Success)
            {
                int level = headingMatch.Groups[1].Value.Length;
                double fontSize = chat != null
                    ? level switch { 1 => baseFontSize * 1.45, 2 => baseFontSize * 1.25, 3 => baseFontSize * 1.08, _ => baseFontSize }
                    : level switch { 1 => baseFontSize * 1.7, 2 => baseFontSize * 1.4, 3 => baseFontSize * 1.2, _ => baseFontSize * 1.1 };
                var fontWeight = level <= 2 || chat != null ? FontWeight.Bold : FontWeight.SemiBold;

                var tb = new ChipSelectableTextBlock
                {
                    FontSize = fontSize,
                    FontWeight = fontWeight,
                    Foreground = new SolidColorBrush(headingColor),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, level <= 2 ? 10 : 6, 0, 4),
                };
                SetInlineText(tb, headingMatch.Groups[2].Value.Trim(), fg, codeBg, codeFg, linkColor, brokenLinkColor, codeFont, wiki);
                controls.Add(tb);
                i++;
                continue;
            }

            // Code block: ```
            if (line.TrimStart().StartsWith("```"))
            {
                var lang = line.TrimStart().Length > 3 ? line.TrimStart()[3..].Trim() : "";
                var codeLines = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
                {
                    codeLines.Add(lines[i]);
                    i++;
                }
                if (i < lines.Length) i++; // skip closing ```

                var codeText = string.Join("\n", codeLines);
                controls.Add(chat != null
                    ? CreateChatCodeBlock(codeText, lang, chat, codeFont, baseFontSize)
                    : CreateCodeBlock(codeText, lang, isDark, codeBg, codeFg, codeFont, baseFontSize));
                continue;
            }

            // Blockquote: >
            if (line.TrimStart().StartsWith(">"))
            {
                var quoteLines = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith(">"))
                {
                    var ql = Regex.Replace(lines[i], @"^>\s?", "");
                    quoteLines.Add(ql);
                    i++;
                }
                var quoteText = string.Join("\n", quoteLines);

                var quoteContent = new ChipSelectableTextBlock
                {
                    FontSize = baseFontSize,
                    FontStyle = FontStyle.Italic,
                    Foreground = new SolidColorBrush(dimColor),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(8, 4),
                };
                SetInlineText(quoteContent, quoteText, dimColor, codeBg, codeFg, linkColor, brokenLinkColor, codeFont, wiki);

                var quoteBorderCtrl = new Border
                {
                    BorderBrush = new SolidColorBrush(quoteBorder),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Background = chat != null ? Brushes.Transparent
                        : new SolidColorBrush(isDark ? Color.FromArgb(20, 100, 160, 255) : Color.FromArgb(15, 0, 100, 200)),
                    Padding = new Thickness(8, 4),
                    Margin = new Thickness(0, 4),
                    Child = quoteContent
                };
                controls.Add(quoteBorderCtrl);
                continue;
            }

            // Horizontal rule: --- or ***
            if (Regex.IsMatch(line.Trim(), @"^[-*_]{3,}$"))
            {
                controls.Add(new Border
                {
                    Height = 1,
                    Background = new SolidColorBrush(ruleColor),
                    Margin = new Thickness(0, chat != null ? 14 : 8),
                });
                i++;
                continue;
            }

            // Unordered list: - or *
            if (Regex.IsMatch(line, @"^\s*[-*+]\s"))
            {
                while (i < lines.Length && Regex.IsMatch(lines[i], @"^\s*[-*+]\s"))
                {
                    var itemText = Regex.Replace(lines[i], @"^\s*[-*+]\s", "");
                    int indent = lines[i].Length - lines[i].TrimStart().Length;

                    var bullet = new SelectableTextBlock
                    {
                        Text = "\u2022",
                        Classes = { ChatSelection.ListMarkerClass },
                        Foreground = new SolidColorBrush(markerColor),
                        FontSize = baseFontSize,
                        Margin = new Thickness(indent * 8 + 4, 0, 6, 0),
                        VerticalAlignment = VerticalAlignment.Top,
                    };
                    var itemContent = new ChipSelectableTextBlock
                    {
                        FontSize = baseFontSize,
                        Foreground = new SolidColorBrush(fg),
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Top,
                    };
                    SetInlineText(itemContent, itemText, fg, codeBg, codeFg, linkColor, brokenLinkColor, codeFont, wiki);

                    // A horizontal StackPanel measures its children with unbounded width, so a
                    // wrapping item would run off the edge. The grid gives the text a real width.
                    var itemPanel = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                        Margin = new Thickness(0, 1),
                    };
                    Grid.SetColumn(itemContent, 1);
                    itemPanel.Children.Add(bullet);
                    itemPanel.Children.Add(itemContent);
                    controls.Add(itemPanel);
                    i++;
                }
                continue;
            }

            // Ordered list: 1. 2. 3.
            if (Regex.IsMatch(line, @"^\s*\d+\.\s"))
            {
                while (i < lines.Length && Regex.IsMatch(lines[i], @"^\s*\d+\.\s"))
                {
                    var match = Regex.Match(lines[i], @"^\s*(\d+)\.\s(.*)$");
                    if (!match.Success) { i++; continue; }

                    var num = new SelectableTextBlock
                    {
                        Text = match.Groups[1].Value + ".",
                        Classes = { ChatSelection.ListMarkerClass },
                        Foreground = new SolidColorBrush(markerColor),
                        FontSize = baseFontSize,
                        Width = 24,
                        TextAlignment = TextAlignment.Right,
                        Margin = new Thickness(4, 0, 6, 0),
                        VerticalAlignment = VerticalAlignment.Top,
                    };
                    var itemContent = new ChipSelectableTextBlock
                    {
                        FontSize = baseFontSize,
                        Foreground = new SolidColorBrush(fg),
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Top,
                    };
                    SetInlineText(itemContent, match.Groups[2].Value, fg, codeBg, codeFg, linkColor, brokenLinkColor, codeFont, wiki);

                    var itemPanel = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                        Margin = new Thickness(0, 1),
                    };
                    Grid.SetColumn(itemContent, 1);
                    itemPanel.Children.Add(num);
                    itemPanel.Children.Add(itemContent);
                    controls.Add(itemPanel);
                    i++;
                }
                continue;
            }

            // Table: | ... |
            if (line.TrimStart().StartsWith("|"))
            {
                var tableRows = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith("|"))
                {
                    tableRows.Add(lines[i]);
                    i++;
                }
                var table = CreateTable(tableRows, isDark, fg, codeBg, codeFg, linkColor, brokenLinkColor, codeFont, wiki, baseFontSize, chat);
                if (table != null)
                    controls.Add(table);
                continue;
            }

            // Regular paragraph
            {
                var paraLines = new List<string>();
                while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i])
                    && !lines[i].TrimStart().StartsWith("#")
                    && !lines[i].TrimStart().StartsWith("```")
                    && !lines[i].TrimStart().StartsWith(">")
                    && !Regex.IsMatch(lines[i], @"^\s*[-*+]\s")
                    && !Regex.IsMatch(lines[i], @"^\s*\d+\.\s")
                    && !Regex.IsMatch(lines[i].Trim(), @"^[-*_]{3,}$")
                    && !lines[i].TrimStart().StartsWith("|"))
                {
                    paraLines.Add(lines[i]);
                    i++;
                }

                // The chat keeps the writer's line breaks, as the desktop app does; joining them
                // with a space also wedges stray gaps into Japanese sentences.
                var paraText = string.Join(chat != null ? "\n" : " ", paraLines);
                var tb = new ChipSelectableTextBlock
                {
                    FontSize = baseFontSize,
                    Foreground = new SolidColorBrush(fg),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2),
                    LineHeight = chat != null ? Math.Round(baseFontSize * 1.625) : 20,
                };
                SetInlineText(tb, paraText, fg, codeBg, codeFg, linkColor, brokenLinkColor, codeFont, wiki);
                controls.Add(tb);
            }
        }

        return controls;
    }

    /// <summary>
    /// Parse inline Markdown formatting (bold, italic, code, links) into TextBlock.Inlines.
    /// </summary>
    private static void SetInlineText(TextBlock tb, string text, Color fg, Color codeBg, Color codeFg,
        Color linkColor, Color brokenLinkColor, Typeface codeFont, WikiLinkOptions? wiki)
    {
        if (string.IsNullOrEmpty(text))
        {
            tb.Text = "";
            return;
        }

        // Simple approach: use regex to find inline patterns and split
        // Patterns: **bold**, *italic*, `code`, [text](url)
        // The wikilink alternative is added only for a caller that can follow one, and it comes
        // first so [[a]] is never mistaken for the [text](url) form. Without it the pattern is
        // character-for-character what it always was, so those callers split exactly as before.
        var pattern = wiki != null
            ? @"(\[\[[^\[\]]+\]\])|(\*\*.*?\*\*)|(\*.*?\*)|(`[^`]+`)|(\[.*?\]\(.*?\))"
            : @"(\*\*.*?\*\*)|(\*.*?\*)|(`[^`]+`)|(\[.*?\]\(.*?\))";
        var parts = Regex.Split(text, pattern);

        bool hasInlines = false;
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;

            if (wiki != null && part.Length > 4 &&
                part.StartsWith("[[", StringComparison.Ordinal) && part.EndsWith("]]", StringComparison.Ordinal))
            {
                // Wikilink [[target]] or [[target|shown text]]
                var inner = part[2..^2];
                int bar = inner.IndexOf('|');
                var target = (bar >= 0 ? inner[..bar] : inner).Trim();
                var shown = (bar >= 0 ? inner[(bar + 1)..] : inner).Trim();
                if (target.Length == 0)
                {
                    tb.Inlines!.Add(new Avalonia.Controls.Documents.Run(part) { Foreground = new SolidColorBrush(fg) });
                    hasInlines = true;
                    continue;
                }

                bool resolves = wiki.Exists(target);
                var navigate = wiki.Navigate;
                AddLink(tb, shown, resolves ? linkColor : brokenLinkColor,
                    resolves ? target : $"{target} — {Loc.Get("MemoryNoteMissing")}",
                    () => navigate(target));
                hasInlines = true;
            }
            else if (Regex.IsMatch(part, @"^\*\*.*\*\*$"))
            {
                // Bold
                var content = part[2..^2];
                tb.Inlines!.Add(new Avalonia.Controls.Documents.Run(content)
                {
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(fg),
                });
                hasInlines = true;
            }
            else if (Regex.IsMatch(part, @"^\*.*\*$") && !part.StartsWith("**"))
            {
                // Italic
                var content = part[1..^1];
                tb.Inlines!.Add(new Avalonia.Controls.Documents.Run(content)
                {
                    FontStyle = FontStyle.Italic,
                    Foreground = new SolidColorBrush(fg),
                });
                hasInlines = true;
            }
            else if (Regex.IsMatch(part, @"^`[^`]+`$"))
            {
                // Inline code
                var content = part[1..^1];
                var codeRun = new Avalonia.Controls.Documents.Run(content)
                {
                    FontFamily = codeFont.FontFamily,
                    Foreground = new SolidColorBrush(codeFg),
                };
                if (_inlineCodeBg != null)
                {
                    // A tinted chip, padded with narrow spaces so the tint clears the glyphs
                    codeRun.Text = " " + content + " ";
                    if (tb is InlineCodeChips.IHost host)
                    {
                        // Drawn by the host as a rounded, outlined chip under the text
                        codeRun.Background = InlineCodeChips.MarkerBrush;
                        host.ChipBackground = _inlineCodeBg;
                        host.ChipBorder = _inlineCodeBorder;
                    }
                    else
                        codeRun.Background = _inlineCodeBg;
                    codeRun.FontSize = tb.FontSize * 0.9;
                }
                tb.Inlines!.Add(codeRun);
                hasInlines = true;
            }
            else if (Regex.IsMatch(part, @"^\[.*?\]\(.*?\)$"))
            {
                // Link [text](url)
                var linkMatch = Regex.Match(part, @"^\[(.*?)\]\((.*?)\)$");
                if (linkMatch.Success)
                {
                    var shown = linkMatch.Groups[1].Value;
                    var target = linkMatch.Groups[2].Value.Trim();
                    var slug = wiki != null && shown.Length > 0 ? NoteTargetOf(target) : null;

                    if (slug != null)
                    {
                        // A note beside this one, written as an ordinary link rather than a
                        // wikilink. It follows the same way, and is marked the same way when the
                        // note it names has not been written.
                        bool resolves = wiki!.Exists(slug);
                        var navigate = wiki.Navigate;
                        AddLink(tb, shown, resolves ? linkColor : brokenLinkColor,
                            resolves ? target : $"{target} — {Loc.Get("MemoryNoteMissing")}",
                            () => navigate(slug));
                    }
                    else if (shown.Length > 0 && wiki?.OpenUrl != null && IsWebUrl(target))
                    {
                        var open = wiki.OpenUrl;
                        AddLink(tb, shown, linkColor, target, () => open(target));
                    }
                    else
                    {
                        tb.Inlines!.Add(new Avalonia.Controls.Documents.Run(shown)
                        {
                            Foreground = new SolidColorBrush(linkColor),
                            TextDecorations = TextDecorations.Underline,
                        });
                    }
                    hasInlines = true;
                }
            }
            else
            {
                // Plain text
                if (hasInlines || parts.Length > 1)
                {
                    tb.Inlines!.Add(new Avalonia.Controls.Documents.Run(part)
                    {
                        Foreground = new SolidColorBrush(fg),
                    });
                    hasInlines = true;
                }
                else
                {
                    tb.Text = text;
                    return;
                }
            }
        }

        if (!hasInlines)
            tb.Text = text;
    }

    /// <summary>
    /// Adds a clickable link to a paragraph. An <c>Inline</c> cannot take pointer events, so the
    /// clickable part is a control the text flows around rather than a Run.
    /// </summary>
    private static void AddLink(TextBlock tb, string shown, Color color, string? tip, Action onClick)
    {
        var link = new TextBlock
        {
            Text = shown,
            Foreground = new SolidColorBrush(color),
            TextDecorations = TextDecorations.Underline,
            FontSize = tb.FontSize,
            FontWeight = tb.FontWeight,
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (tip != null) ToolTip.SetTip(link, tip);

        link.PointerPressed += (_, e) =>
        {
            onClick();
            e.Handled = true;
        };

        tb.Inlines!.Add(new Avalonia.Controls.Documents.InlineUIContainer(link));
    }

    /// <summary>
    /// The note a Markdown link names, or null if it points anywhere else. MEMORY.md writes its
    /// index as <c>[title](slug.md)</c> rather than as wikilinks, so those rows have to reach the
    /// note beside them the same way. A target carrying a directory, a scheme or another extension
    /// leads out of the memory folder and is left as plain coloured text.
    /// </summary>
    private static string? NoteTargetOf(string target)
    {
        var t = target;
        int hash = t.IndexOf('#');
        if (hash >= 0) t = t[..hash];
        if (t.StartsWith("./", StringComparison.Ordinal)) t = t[2..];
        if (t.Length == 0 || t.IndexOfAny(Pathish) >= 0) return null;
        return t.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? t[..^3] : null;
    }

    /// <summary>Characters that mean a link target is a path or a URL, not a note beside this one.</summary>
    private static readonly char[] Pathish = { '/', '\\', ':' };

    private static bool IsWebUrl(string target) =>
        target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static Border CreateCodeBlock(string code, string language, bool isDark,
        Color codeBg, Color codeFg, Typeface codeFont, double baseFontSize = 13)
    {
        var headerPanel = new DockPanel
        {
            Margin = new Thickness(0, 0, 0, 0),
        };

        if (!string.IsNullOrEmpty(language))
        {
            var langLabel = new TextBlock
            {
                Text = language,
                FontSize = 11,
                Foreground = new SolidColorBrush(isDark ? Color.FromRgb(140, 140, 145) : Color.FromRgb(84, 84, 92)),
                Margin = new Thickness(10, 4, 0, 0),
            };
            DockPanel.SetDock(langLabel, Avalonia.Controls.Dock.Left);
            headerPanel.Children.Add(langLabel);
        }

        var copyBtn = new Button
        {
            Content = Loc.Get("CopyCode", "Copy"),
            FontSize = 10,
            Padding = new Thickness(6, 2),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(isDark ? Color.FromRgb(140, 140, 145) : Color.FromRgb(84, 84, 92)),
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 2, 6, 0),
        };
        var codeForCopy = code; // capture for closure
        copyBtn.Click += async (_, _) =>
        {
            var topLevel = TopLevel.GetTopLevel(copyBtn);
            if (topLevel?.Clipboard != null)
                await topLevel.Clipboard.SetTextAsync(codeForCopy);
            copyBtn.Content = "\u2713"; // checkmark
            await System.Threading.Tasks.Task.Delay(1500);
            copyBtn.Content = Loc.Get("CopyCode", "Copy");
        };
        DockPanel.SetDock(copyBtn, Avalonia.Controls.Dock.Right);
        headerPanel.Children.Add(copyBtn);
        headerPanel.Children.Add(new Border()); // filler

        var codeTextBlock = new SelectableTextBlock
        {
            Text = code,
            FontSize = baseFontSize * 0.92,
            FontFamily = new FontFamily(codeFont.FontFamily.Name),
            Foreground = new SolidColorBrush(codeFg),
            Margin = new Thickness(10, 2, 10, 8),
            TextWrapping = TextWrapping.Wrap,
        };

        var codeStack = new StackPanel();
        codeStack.Children.Add(headerPanel);
        codeStack.Children.Add(codeTextBlock);

        return new Border
        {
            Background = new SolidColorBrush(codeBg),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 4),
            Child = codeStack,
        };
    }

    /// <summary>
    /// A code block as the desktop app draws it: a hairline-bordered card with the language on
    /// the left, a copy icon on the right and string literals / comments picked out in colour.
    /// </summary>
    private static Border CreateChatCodeBlock(string code, string language, ChatPalette pal,
        Typeface codeFont, double baseFontSize)
    {
        var header = new DockPanel { Margin = new Thickness(12, 6, 6, 0) };

        var copyIcon = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M5,5 H13 V13 H5 Z M3,10 H1 V1 H10 V3"),
            Stroke = new SolidColorBrush(pal.Dim),
            StrokeThickness = 1.2,
            Width = 13, Height = 13,
            Stretch = Stretch.Uniform,
        };
        var copyBtn = new Button
        {
            Content = copyIcon,
            Padding = new Thickness(6),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Top,
        };
        ToolTip.SetTip(copyBtn, Loc.Get("CopyCode", "Copy"));
        var codeForCopy = code;
        copyBtn.Click += async (_, _) =>
        {
            var topLevel = TopLevel.GetTopLevel(copyBtn);
            if (topLevel?.Clipboard != null)
                await topLevel.Clipboard.SetTextAsync(codeForCopy);
            copyBtn.Content = new TextBlock { Text = "✓", FontSize = 12, Foreground = new SolidColorBrush(pal.Dim) };
            await System.Threading.Tasks.Task.Delay(1500);
            copyBtn.Content = copyIcon;
        };
        DockPanel.SetDock(copyBtn, Avalonia.Controls.Dock.Right);
        header.Children.Add(copyBtn);
        header.Children.Add(new TextBlock
        {
            Text = language,
            FontSize = Math.Max(10, baseFontSize * 0.8),
            Foreground = new SolidColorBrush(pal.Dim),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var codeText = new SelectableTextBlock
        {
            FontSize = baseFontSize * 0.9,
            FontFamily = codeFont.FontFamily,
            Foreground = new SolidColorBrush(pal.CodeFg),
            Margin = new Thickness(16, 0, 16, 12),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = Math.Round(baseFontSize * 1.45),
        };
        // Subpixel AA draws mono strokes hairline-thin; the desktop app's grayscale AA
        // is what gives its code its weight.
        TextOptions.SetTextRenderingMode(codeText, TextRenderingMode.Antialias);
        if (IsDiff(code, language)) HighlightDiff(codeText, code, pal);
        else HighlightCode(codeText, code, pal);

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(codeText);

        return new Border
        {
            Background = new SolidColorBrush(pal.CodeBg),
            BorderBrush = new SolidColorBrush(pal.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 6),
            Child = stack,
        };
    }

    /// <summary>
    /// Language-agnostic colouring: string literals in green (except JSON keys, which stay in
    /// ink), line comments dimmed. Enough to read structure at a glance without a real lexer.
    /// Very long blocks are left plain so a huge paste never stalls the UI thread.
    /// </summary>
    /// <summary>
    /// A block tagged diff/patch, or an untagged one that carries a unified-diff hunk header.
    /// </summary>
    private static bool IsDiff(string code, string language)
    {
        var lang = language.Trim().ToLowerInvariant();
        if (lang is "diff" or "patch" or "udiff") return true;
        if (lang.Length > 0) return false;
        return code.StartsWith("@@ ") || code.Contains("\n@@ ")
            || (code.StartsWith("--- ") && code.Contains("\n+++ "));
    }

    /// <summary>
    /// Unified-diff colouring as the desktop app shows it: added lines green, removed lines red,
    /// file headers dimmed, hunk headers and context lines in ink.
    /// </summary>
    private static void HighlightDiff(TextBlock tb, string code, ChatPalette pal)
    {
        if (code.Length > 20000) { tb.Text = code; return; }

        var inlines = tb.Inlines!;
        var lines = code.Split('\n');
        for (int n = 0; n < lines.Length; n++)
        {
            var line = lines[n].TrimEnd('\r');
            var text = n < lines.Length - 1 ? line + "\n" : line;
            if (text.Length == 0) continue;
            Color? color = line.StartsWith("+++ ") || line.StartsWith("--- ") ? pal.Dim
                : line.StartsWith('+') ? pal.DiffAddFg
                : line.StartsWith('-') ? pal.DiffDelFg
                : null;
            var run = new Avalonia.Controls.Documents.Run(text);
            if (color is { } c) run.Foreground = new SolidColorBrush(c);
            inlines.Add(run);
        }
    }

    private static void HighlightCode(TextBlock tb, string code, ChatPalette pal)
    {
        if (code.Length > 20000) { tb.Text = code; return; }

        var inlines = tb.Inlines!;
        var plain = new System.Text.StringBuilder();
        void Flush()
        {
            if (plain.Length == 0) return;
            inlines.Add(new Avalonia.Controls.Documents.Run(plain.ToString()));
            plain.Clear();
        }
        void Add(string text, Color color, bool italic = false)
        {
            Flush();
            inlines.Add(new Avalonia.Controls.Documents.Run(text)
            {
                Foreground = new SolidColorBrush(color),
                FontStyle = italic ? FontStyle.Italic : FontStyle.Normal,
            });
        }

        int i = 0;
        while (i < code.Length)
        {
            char c = code[i];
            if (c is '"' or '\'' or '`')
            {
                int j = i + 1;
                while (j < code.Length && code[j] != c && code[j] != '\n')
                    j += code[j] == '\\' && j + 1 < code.Length ? 2 : 1;
                if (j < code.Length && code[j] == c)
                {
                    int k = j + 1;
                    while (k < code.Length && code[k] is ' ' or '\t') k++;
                    bool isKey = c == '"' && k < code.Length && code[k] == ':';
                    var literal = code[i..(j + 1)];
                    if (isKey) plain.Append(literal); else Add(literal, pal.StringFg);
                    i = j + 1;
                    continue;
                }
            }
            bool lineComment = (c == '/' && i + 1 < code.Length && code[i + 1] == '/')
                || (c == '#' && (i == 0 || code[i - 1] is '\n' or ' ' or '\t'));
            if (lineComment)
            {
                int end = code.IndexOf('\n', i);
                if (end < 0) end = code.Length;
                Add(code[i..end], pal.CommentFg, italic: true);
                i = end;
                continue;
            }
            plain.Append(c);
            i++;
        }
        Flush();
    }

    private static Control? CreateTable(List<string> rows, bool isDark, Color fg, Color codeBg, Color codeFg, Color linkColor, Color brokenLinkColor, Typeface codeFont, WikiLinkOptions? wiki, double baseFontSize = 13, ChatPalette? chat = null)
    {
        if (rows.Count < 2) return null;

        var parsedRows = new List<string[]>();
        foreach (var row in rows)
        {
            var cells = row.Trim().Trim('|').Split('|');
            for (int j = 0; j < cells.Length; j++)
                // A table row is one source line, so a line break inside a cell can only be written as <br>.
                cells[j] = Regex.Replace(cells[j].Trim(), @"\s*<br\s*/?>\s*", "\n", RegexOptions.IgnoreCase);
            // Skip separator rows (--- | --- | ---)
            if (cells.Length > 0 && Regex.IsMatch(cells[0], @"^[-:]+$"))
                continue;
            parsedRows.Add(cells);
        }

        if (parsedRows.Count == 0) return null;
        int colCount = parsedRows[0].Length;

        var grid = new Grid
        {
            Margin = new Thickness(0, 4),
        };

        for (int c = 0; c < colCount; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        for (int r = 0; r < parsedRows.Count; r++)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        if (chat != null)
        {
            // Desktop style: one rounded outline, a tinted header row and hairlines between
            // rows only.
            grid.Margin = new Thickness(0);
            var line = new SolidColorBrush(chat.Border);
            for (int r = 0; r < parsedRows.Count; r++)
            {
                for (int c = 0; c < colCount; c++)
                {
                    var cellText = new ChipSelectableTextBlock
                    {
                        FontSize = baseFontSize * 0.95,
                        FontWeight = r == 0 ? FontWeight.SemiBold : FontWeight.Normal,
                        Foreground = new SolidColorBrush(fg),
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    SetInlineText(cellText, c < parsedRows[r].Length ? parsedRows[r][c] : "",
                        fg, codeBg, codeFg, linkColor, brokenLinkColor, codeFont, wiki);
                    var cell = new Border
                    {
                        Background = r == 0 ? new SolidColorBrush(chat.TableHeaderBg) : Brushes.Transparent,
                        BorderBrush = line,
                        BorderThickness = new Thickness(0, 0, 0, r < parsedRows.Count - 1 ? 1 : 0),
                        Padding = new Thickness(12, 7),
                        Child = cellText,
                    };
                    Grid.SetRow(cell, r);
                    Grid.SetColumn(cell, c);
                    grid.Children.Add(cell);
                }
            }
            return new Border
            {
                BorderBrush = line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                ClipToBounds = true,
                Margin = new Thickness(0, 6),
                Child = grid,
            };
        }

        for (int r = 0; r < parsedRows.Count; r++)
        {
            for (int c = 0; c < Math.Min(parsedRows[r].Length, colCount); c++)
            {
                var cellBorder = new Border
                {
                    BorderBrush = new SolidColorBrush(isDark ? Color.FromRgb(60, 60, 65) : Color.FromRgb(200, 200, 205)),
                    BorderThickness = new Thickness(0.5),
                    Background = r == 0
                        ? new SolidColorBrush(codeBg)
                        : Brushes.Transparent,
                    Padding = new Thickness(6, 3),
                };
                var cellText = new ChipSelectableTextBlock
                {
                    FontSize = baseFontSize * 0.92,
                    FontWeight = r == 0 ? FontWeight.SemiBold : FontWeight.Normal,
                    Foreground = new SolidColorBrush(fg),
                    TextWrapping = TextWrapping.Wrap,
                };
                SetInlineText(cellText, parsedRows[r][c], fg, codeBg, codeFg, linkColor, brokenLinkColor, codeFont, wiki);
                cellBorder.Child = cellText;
                Grid.SetRow(cellBorder, r);
                Grid.SetColumn(cellBorder, c);
                grid.Children.Add(cellBorder);
            }
        }

        return grid;
    }
}
