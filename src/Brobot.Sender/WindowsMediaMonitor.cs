using Windows.Media.Control;

namespace Brobot.Sender;

public sealed record NowPlaying(string Title, string Artist, string SourceAppId)
{
    /// <summary>Spotify (audio) gets FACE MUSIC; anything else (a browser tab, VLC, etc. — likely video) gets FACE WATCHING.</summary>
    public bool IsLikelyAudioOnly => SourceAppId.Contains("spotify", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Watches Windows' native "now playing" session (the same one the volume
/// flyout's media controls use) via GlobalSystemMediaTransportControlsSessionManager —
/// no per-app API/login needed, works for Spotify, browser tabs playing
/// YouTube, VLC, etc. Raises <see cref="NowPlayingChanged"/> with null when
/// nothing is actively playing.
/// </summary>
public sealed class WindowsMediaMonitor : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private NowPlaying? _lastRaised;
    private bool _lastRaisedWasNull = true;

    public event Action<NowPlaying?>? NowPlayingChanged;

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        AttachToCurrentSession();
    }

    public void Stop()
    {
        if (_manager != null)
        {
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        }
        DetachSession();
        _manager = null;
    }

    public void Dispose() => Stop();

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        => AttachToCurrentSession();

    private void AttachToCurrentSession()
    {
        DetachSession();

        _session = _manager?.GetCurrentSession();
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnSessionChanged;
            _session.PlaybackInfoChanged += OnSessionChanged;
        }

        _ = RefreshAsync();
    }

    private void DetachSession()
    {
        if (_session == null)
        {
            return;
        }

        _session.MediaPropertiesChanged -= OnSessionChanged;
        _session.PlaybackInfoChanged -= OnSessionChanged;
        _session = null;
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args) => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        GlobalSystemMediaTransportControlsSession? session = _session;
        if (session == null)
        {
            RaiseIfChanged(null);
            return;
        }

        bool isPlaying = session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        if (!isPlaying)
        {
            RaiseIfChanged(null);
            return;
        }

        var props = await session.TryGetMediaPropertiesAsync();
        string title = props.Title ?? string.Empty;
        if (title.Length == 0)
        {
            RaiseIfChanged(null);
            return;
        }

        RaiseIfChanged(new NowPlaying(title, props.Artist ?? string.Empty, session.SourceAppUserModelId ?? string.Empty));
    }

    /// <summary>
    /// PlaybackInfoChanged fires for things that don't matter to us too (seek,
    /// volume, etc.), so only raise the event — and only send FACE/MSG to
    /// Core — when the effective now-playing state actually changed.
    /// </summary>
    private void RaiseIfChanged(NowPlaying? current)
    {
        bool isNull = current == null;
        if (isNull == _lastRaisedWasNull && Equals(current, _lastRaised))
        {
            return;
        }

        _lastRaised = current;
        _lastRaisedWasNull = isNull;
        NowPlayingChanged?.Invoke(current);
    }
}
