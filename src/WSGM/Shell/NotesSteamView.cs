using System.Threading;

namespace WSGM.Shell;

/// <summary>Where Steam's Notes page is and what it last reported.</summary>
/// <remarks>Guarded by its own lock; the publication reads a consistent copy.</remarks>
internal sealed class NotesSteamView
{
    private readonly Lock _gate = new();
    private uint _appId;
    private string? _capabilities;
    private string? _notice;
    private bool _opened;
    private string? _pageId;
    private uint _processId;
    private string? _scope;

    /// <summary>A consistent copy.</summary>
    internal (bool Opened, string? Scope, string? PageId, uint AppId, uint ProcessId, string? Notice) Read()
    {
        lock (_gate)
        {
            return (_opened, _scope, _pageId, _appId, _processId, _notice);
        }
    }

    /// <summary>Records the overlay window the page runs in.</summary>
    /// <param name="appId">The game's app id, or zero outside a game overlay.</param>
    /// <param name="processId">The game process, or zero.</param>
    /// <returns>Whether the window changed.</returns>
    internal bool SetWindow(uint appId, uint processId)
    {
        lock (_gate)
        {
            var changed = !_opened || _appId != appId || _processId != processId;
            _opened = true;
            _appId = appId;
            _processId = processId;
            return changed;
        }
    }

    /// <summary>Selects a notebook and page; null page shows the list.</summary>
    internal void Select(string? scope, string? pageId)
    {
        lock (_gate)
        {
            _scope = scope;
            _pageId = pageId;
        }
    }

    /// <summary>Sets or clears the page's notice line.</summary>
    internal void SetNotice(string? notice)
    {
        lock (_gate)
        {
            _notice = notice;
        }
    }

    /// <summary>Records the browser capabilities the page reported, once per distinct report.</summary>
    /// <param name="report">The report.</param>
    /// <returns>Whether it differs from the last one.</returns>
    internal bool RecordCapabilities(string report)
    {
        lock (_gate)
        {
            if (_capabilities == report)
            {
                return false;
            }

            _capabilities = report;
            return true;
        }
    }
}
