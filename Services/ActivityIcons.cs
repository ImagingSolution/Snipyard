using System.Collections.Generic;
using System.Linq;

namespace Snipyard.Services;

/// <summary>
/// The icons a custom activity-bar button can wear, as 24-unit path data in the same style
/// as the built-in activity buttons. Settings store the key, so the path can be retouched
/// without touching anyone's appsettings.json.
/// </summary>
public static class ActivityIcons
{
    public sealed record Icon(string Key, string Data, double Size);

    public static readonly IReadOnlyList<Icon> All = new Icon[]
    {
        new("compact", "M4 1H20V3H4Z M6 4L18 8L6 12L12 12L22 8L12 4Z M2 12L14 16L2 20L8 20L18 16L8 12Z M4 21H20V23H4Z M10 3H14L12 5Z M10 21L12 19L14 21Z", 22),
        new("bolt", "M13 2L4 14H11L10 22L20 9H13Z", 20),
        new("star", "M12 2L14.9 8.6L22 9.3L16.6 14L18.2 21L12 17.3L5.8 21L7.4 14L2 9.3L9.1 8.6Z", 20),
        new("prompt", "M4 4H20C21.1 4 22 4.9 22 6V18C22 19.1 21.1 20 20 20H4C2.9 20 2 19.1 2 18V6C2 4.9 2.9 4 4 4ZM4 6V18H20V6ZM6.5 9.5L7.9 8.1L11.8 12L7.9 15.9L6.5 14.5L9 12ZM12 14H17V16H12Z", 20),
        new("send", "M2 21L23 12L2 3V10L17 12L2 14Z", 20),
        new("sparkle", "M11 2L13.2 8.8L20 11L13.2 13.2L11 20L8.8 13.2L2 11L8.8 8.8Z M19 14L19.9 16.1L22 17L19.9 17.9L19 20L18.1 17.9L16 17L18.1 16.1Z", 20),
        new("eraser", "M4 13L8 9L15 16L11 20Z M9 8L14 3L21 10L16 15Z M11.55 8L14 5.55L18.45 10L16 12.45Z M13 20H21V21.8H13Z", 20),
    };

    /// <summary>The icon a new button starts on: the first one that is not compact's.</summary>
    public static Icon DefaultForNew => All[1];

    /// <summary>An unknown key (a hand-edited or newer settings file) falls back to the first icon.</summary>
    public static Icon Get(string key) => All.FirstOrDefault(i => i.Key == key) ?? All[0];
}
