using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Snipyard.Services;
using Microsoft.Web.WebView2.Core;

namespace Snipyard.Controls;

/// <summary>
/// The side pane's Preview tab: a WebView2 browser for the dev server the session started. A
/// localhost address seen in the terminal is offered here, and anything else can be typed in.
/// </summary>
public sealed class BrowserPreviewPanel : DockPanel
{
    private static readonly Regex LocalUrl = new(
        @"https?://(?:localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1\]):\d{2,5}(?:/[^\s""'<>)\]\u001b]*)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly TextBox _url;
    private readonly Button _back;
    private readonly Button _reload;
    private readonly Button _external;
    private readonly Border _suggestBar;
    private readonly TextBlock _suggestText;
    private readonly Button _openButton;
    private readonly Panel _body;
    private readonly TextBlock _message;
    private WebViewHost? _host;
    private string? _suggested;
    private string? _dismissed;
    private bool _isDark;

    public BrowserPreviewPanel(bool isDark)
    {
        _isDark = isDark;

        _back = ToolButton("←", () => _host?.GoBack());
        _reload = ToolButton("⟳", () => _host?.Reload());
        ToolTip.SetTip(_reload, Loc.Get("PreviewReload"));
        _external = ToolButton("↗", () => OpenExternally(_host?.CurrentUrl ?? _url!.Text));
        ToolTip.SetTip(_external, Loc.Get("PreviewOpenExternal"));
        _url = new TextBox
        {
            FontSize = 12,
            MinHeight = 26,
            Padding = new Thickness(8, 3),
            VerticalContentAlignment = VerticalAlignment.Center,
            PlaceholderText = "http://localhost:3000",
        };
        _url.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Navigate(_url.Text);
            e.Handled = true;
        };

        var toolbar = new DockPanel { Margin = new Thickness(8, 6), LastChildFill = true };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 0, 6, 0) };
        left.Children.Add(_back);
        left.Children.Add(_reload);
        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(_external, Dock.Right);
        _external.Margin = new Thickness(6, 0, 0, 0);
        toolbar.Children.Add(left);
        toolbar.Children.Add(_external);
        toolbar.Children.Add(_url);
        DockPanel.SetDock(toolbar, Dock.Top);
        Children.Add(toolbar);

        _suggestText = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var open = ToolButton(Loc.Get("PreviewOpen"), () => { if (_suggested != null) Navigate(_suggested); });
        _openButton = open;
        open.Foreground = Brushes.White;
        var dismiss = ToolButton("✕", () => { _dismissed = _suggested; _suggestBar!.IsVisible = false; });
        var sb = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        sb.Children.Add(open);
        sb.Children.Add(dismiss);
        var suggestRow = new DockPanel();
        DockPanel.SetDock(sb, Dock.Right);
        suggestRow.Children.Add(sb);
        suggestRow.Children.Add(_suggestText);
        _suggestBar = new Border { Padding = new Thickness(10, 5), Child = suggestRow, IsVisible = false };
        DockPanel.SetDock(_suggestBar, Dock.Top);
        Children.Add(_suggestBar);

        _message = new TextBlock
        {
            Text = Loc.Get("PreviewEmpty"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(20),
        };
        _body = new Panel();
        _body.Children.Add(_message);
        Children.Add(_body);

        ApplyTheme(isDark);
    }

    /// <summary>Picks a local server address out of terminal output and offers it.</summary>
    public void OfferFromOutput(string text)
    {
        var m = LocalUrl.Match(text);
        if (!m.Success) return;
        var url = m.Value.TrimEnd('.', ',', ';', ':').Replace("0.0.0.0", "localhost");
        if (url == _suggested || url == _dismissed || url == _host?.CurrentUrl) return;
        _suggested = url;
        _suggestText.Text = string.Format(Loc.Get("PreviewDetected"), url);
        _suggestBar.IsVisible = true;
    }

    public void Navigate(string? url)
    {
        url = url?.Trim();
        if (string.IsNullOrEmpty(url)) return;
        if (!url.Contains("://")) url = "http://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https" && uri.Scheme != "file"))
            return;
        _url.Text = uri.ToString();
        if (uri.ToString() == _suggested) _suggestBar.IsVisible = false;

        if (!OperatingSystem.IsWindows())
        {
            // WebView2 is Windows-only; the system browser stands in for the embedded one.
            OpenExternally(uri.ToString());
            _message.Text = Loc.Get("PreviewOpenedExternally");
            return;
        }
        if (!WebViewHost.RuntimeAvailable(out var version))
        {
            _message.Text = Loc.Get("PreviewNoRuntime");
            return;
        }
        _ = version;
        if (_host == null)
        {
            _host = new WebViewHost();
            _host.UrlChanged += u => _url.Text = u;
            _host.Failed += err => { _message.Text = err; _body.Children.Remove(_host!); _host = null; };
            _body.Children.Add(_host);
        }
        _host.Navigate(uri.ToString());
    }

    public void ApplyTheme(bool isDark)
    {
        _isDark = isDark;
        _openButton.Background = new SolidColorBrush(ChatTheme.Accent(isDark));
        var fg = new SolidColorBrush(isDark ? Color.FromRgb(225, 225, 228) : Color.FromRgb(35, 35, 38));
        Background = new SolidColorBrush(ChatTheme.Background(isDark));
        _message.Foreground = new SolidColorBrush(isDark ? Color.FromRgb(150, 150, 155) : Color.FromRgb(105, 105, 110));
        _suggestBar.Background = new SolidColorBrush(ChatTheme.Surface(isDark));
        _suggestText.Foreground = fg;
        foreach (var b in new[] { _back, _reload, _external }) b.Foreground = fg;
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
            BorderBrush = new SolidColorBrush(ChatTheme.Outline(_isDark)),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static void OpenExternally(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }
}

/// <summary>
/// A WebView2 controller living in a child HWND that Avalonia positions. Leaving the visual tree
/// (a tab switch) destroys the window, so the controller is re-created on return at the last URL.
/// </summary>
internal sealed class WebViewHost : NativeControlHost
{
    private static CoreWebView2Environment? _env;
    private IntPtr _hwnd;
    private CoreWebView2Controller? _controller;
    private string? _pending;
    private bool _creating;

    public string? CurrentUrl { get; private set; }
    public event Action<string>? UrlChanged;
    public event Action<string>? Failed;

    public static bool RuntimeAvailable(out string? version)
    {
        version = null;
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return !string.IsNullOrEmpty(version);
        }
        catch
        {
            version = null;
            return false;
        }
    }

    public void Navigate(string url)
    {
        CurrentUrl = url;
        if (_controller != null) _controller.CoreWebView2.Navigate(url);
        else _pending = url;
    }

    public void Reload() => _controller?.CoreWebView2.Reload();

    public void GoBack()
    {
        if (_controller?.CoreWebView2.CanGoBack == true) _controller.CoreWebView2.GoBack();
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _hwnd = CreateWindowEx(0, "Static", "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
            0, 0, 1, 1, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        _pending ??= CurrentUrl;
        _ = CreateControllerAsync(_hwnd);
        return new PlatformHandle(_hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        try { _controller?.Close(); } catch { }
        _controller = null;
        DestroyWindow(control.Handle);
        _hwnd = IntPtr.Zero;
    }

    private async System.Threading.Tasks.Task CreateControllerAsync(IntPtr hwnd)
    {
        if (_creating) return;
        _creating = true;
        try
        {
            if (_env == null)
            {
                var data = Path.Combine(AppPaths.Local, "WebView2");
                _env = await CoreWebView2Environment.CreateAsync(null, data);
            }
            var controller = await _env.CreateCoreWebView2ControllerAsync(hwnd);
            if (hwnd != _hwnd)
            {
                // The window went away while this was starting up
                controller.Close();
                return;
            }
            _controller = controller;
            controller.CoreWebView2.SourceChanged += (_, _) =>
            {
                CurrentUrl = controller.CoreWebView2.Source;
                UrlChanged?.Invoke(CurrentUrl);
            };
            FitController();
            if (_pending != null)
            {
                controller.CoreWebView2.Navigate(_pending);
                _pending = null;
            }
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex.Message);
        }
        finally
        {
            _creating = false;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
            Dispatcher.UIThread.Post(FitController, DispatcherPriority.Background);
    }

    private void FitController()
    {
        if (_controller == null || _hwnd == IntPtr.Zero) return;
        if (!GetClientRect(_hwnd, out var r)) return;
        _controller.Bounds = new System.Drawing.Rectangle(0, 0, r.Right - r.Left, r.Bottom - r.Top);
    }

    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPCHILDREN = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
}
