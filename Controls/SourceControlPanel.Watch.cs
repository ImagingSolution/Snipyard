using System;
using System.IO;
using Avalonia.Threading;

namespace Snipyard.Controls;

/// <summary>
/// Keeps the panel in step with edits made outside it - an editor saving a file, a build, a
/// git command typed into a terminal. The app-wide watcher only follows names (for the file
/// tree), so a file whose contents changed in place never reached this panel until ⟳.
/// </summary>
public sealed partial class SourceControlPanel
{
    private FileSystemWatcher? _repoWatcher;
    private string _repoWatcherPath = "";
    private DispatcherTimer? _repoWatchDebounce;

    // The panel's own reload makes git touch .git/index, which would otherwise wake the watcher
    // and reload again. Events landing this soon after a watcher-driven reload are ignored.
    private DateTime _repoWatchQuietUntil = DateTime.MinValue;

    /// <summary>Watches the current repository while the panel is showing, and nothing otherwise.</summary>
    private void UpdateRepoWatcher()
    {
        var want = _panelShown && _repo.Length > 0 && Directory.Exists(_repo) ? _repo : "";
        if (string.Equals(want, _repoWatcherPath, StringComparison.OrdinalIgnoreCase)) return;

        _repoWatcher?.Dispose();
        _repoWatcher = null;
        _repoWatcherPath = want;
        if (want.Length == 0) return;

        try
        {
            var watcher = new FileSystemWatcher(want)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                    | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            watcher.Changed += OnRepoFileEvent;
            watcher.Created += OnRepoFileEvent;
            watcher.Deleted += OnRepoFileEvent;
            watcher.Renamed += OnRepoFileEvent;
            // An overflow means too much happened to list; one reload covers all of it.
            watcher.Error += (_, _) => ScheduleRepoWatchRefresh();
            watcher.EnableRaisingEvents = true;
            _repoWatcher = watcher;
        }
        catch
        {
            // A folder that cannot be watched (network share, permissions) still has ⟳.
            _repoWatcherPath = "";
        }
    }

    private void OnRepoFileEvent(object sender, FileSystemEventArgs e)
    {
        if (DateTime.UtcNow < _repoWatchQuietUntil) return;
        if (!IsInterestingRepoPath(e.FullPath)) return;
        ScheduleRepoWatchRefresh();
    }

    /// <summary>
    /// Everything in the working tree counts. Inside .git only what an outside commit, checkout,
    /// stage or stash rewrites does - objects, logs and lock files churn without telling the panel
    /// anything new.
    /// </summary>
    private bool IsInterestingRepoPath(string fullPath)
    {
        var root = _repoWatcherPath;
        if (root.Length == 0 || !fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;

        var relative = fullPath.Substring(root.Length).TrimStart('\\', '/');
        var gitPrefix = ".git" + Path.DirectorySeparatorChar;
        if (relative.Equals(".git", StringComparison.OrdinalIgnoreCase)) return false;
        if (!relative.StartsWith(gitPrefix, StringComparison.OrdinalIgnoreCase)) return true;

        var inner = relative.Substring(gitPrefix.Length);
        if (inner.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) return false;
        return inner.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
            || inner.Equals("index", StringComparison.OrdinalIgnoreCase)
            || inner.Equals("MERGE_HEAD", StringComparison.OrdinalIgnoreCase)
            || inner.Equals("CHERRY_PICK_HEAD", StringComparison.OrdinalIgnoreCase)
            || inner.Equals("REVERT_HEAD", StringComparison.OrdinalIgnoreCase)
            || inner.StartsWith("refs" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || inner.StartsWith("rebase-", StringComparison.OrdinalIgnoreCase);
    }

    private void ScheduleRepoWatchRefresh()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_repoWatchDebounce == null)
            {
                _repoWatchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                _repoWatchDebounce.Tick += async (_, _) =>
                {
                    _repoWatchDebounce.Stop();
                    if (!_panelShown || _repo.Length == 0) return;

                    // A git action of the panel's own reloads when it finishes; try again after.
                    if (_busy) { _repoWatchDebounce.Start(); return; }

                    await RefreshAsync();
                    _repoWatchQuietUntil = DateTime.UtcNow.AddSeconds(1);
                };
            }
            _repoWatchDebounce.Stop();
            _repoWatchDebounce.Start();
        });
    }
}
