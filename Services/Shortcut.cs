using System;
using Avalonia.Input;

namespace Snipyard.Services;

/// <summary>
/// The modifier app shortcuts are built on: Command on macOS, Ctrl everywhere else.
///
/// On macOS Ctrl keeps its terminal meaning (Ctrl+C interrupts, Ctrl+W deletes a word, ...),
/// so a shortcut written against <see cref="Primary"/> never takes a control character away
/// from the CLI there. Keys that only exist as terminal control characters (Ctrl+D, Ctrl+L)
/// stay on <see cref="KeyModifiers.Control"/> and should not use this.
/// </summary>
public static class Shortcut
{
    public static bool IsMac { get; } = OperatingSystem.IsMacOS();

    /// <summary>Command (reported by Avalonia as Meta) on macOS, Ctrl elsewhere.</summary>
    public static KeyModifiers Primary { get; } = IsMac ? KeyModifiers.Meta : KeyModifiers.Control;

    /// <summary>
    /// True when the primary modifier is held. On macOS Ctrl+Cmd is not counted, so the
    /// chord stays free for the system and for a terminal-bound Ctrl key.
    /// </summary>
    public static bool HasPrimary(KeyModifiers mods) =>
        mods.HasFlag(Primary) && (!IsMac || !mods.HasFlag(KeyModifiers.Control));

    /// <summary>
    /// <paramref name="mods"/> reduced to Primary/Shift/Alt, for exact comparisons such as
    /// <c>Mods(e.KeyModifiers) == (Shortcut.Primary | KeyModifiers.Shift)</c>. On macOS a held
    /// Ctrl is kept as well, so it makes the comparison fail instead of being ignored.
    /// </summary>
    public static KeyModifiers Mods(KeyModifiers mods) =>
        mods & (Primary | KeyModifiers.Shift | KeyModifiers.Alt | (IsMac ? KeyModifiers.Control : 0));

    /// <summary>A shortcut label written the Windows way ("Ctrl+Shift+P"), shown as "Cmd+Shift+P" on macOS.</summary>
    public static string Label(string windowsLabel) =>
        IsMac ? windowsLabel.Replace("Ctrl+", "Cmd+") : windowsLabel;
}
