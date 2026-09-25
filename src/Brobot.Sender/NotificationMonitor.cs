using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Brobot.Sender;

/// <summary>
/// AppUserModelId is carried alongside DisplayName/Text purely so
/// NotificationClassifier has something more specific than a display name
/// to key on later -- a browser's own per-site notifications (Gmail in a
/// Chrome/Edge tab, say) share the browser's generic DisplayName but each
/// get their own AppUserModelId of the shape
/// "&lt;browser AUMID&gt;!&lt;origin URL&gt;" (confirmed against a real
/// per-site entry, for a different site, in this machine's own toast
/// settings registry key) -- not yet used by any classification rule
/// (nothing here has confirmed the exact Gmail value yet), but there's no
/// reason to throw the data away since GetNotificationsAsync already hands
/// it over for free.
///
/// Title is ToastGeneric's own first text element, which is *not* always
/// "the title" in the everyday sense -- for a new-mail toast specifically
/// it's the sender's name, and the actual subject line is the second
/// element, Subtitle. Confirmed the hard way: MainWindow's EMAIL text
/// originally used Title alone and only ever showed the sender, never the
/// subject, until this was reported directly. Meeting reminders don't
/// carry this same split (their own Title, the event's own name, is
/// exactly what NotificationClassifier already uses), so Subtitle exists
/// purely for Email's benefit -- null whenever a toast has fewer than two
/// text elements.
/// </summary>
public sealed record PcNotification(string AppName, string Text, string? Title = null, string? AppUserModelId = null, string? Subtitle = null);

/// <summary>
/// Watches every toast notification the Windows notification platform shows
/// (any app, not just this one) via UserNotificationListener -- the same
/// WinRT surface Action Center itself is built on. Despite this API's own
/// docs historically gating it behind package identity (MSIX/UWP),
/// RequestAccessAsync and GetNotificationsAsync both work unmodified from
/// this app's plain Win32 process on this Windows build, with zero extra
/// packaging -- proven by a standalone throwaway test before this class was
/// written, the same way WindowsMediaMonitor/WeatherMonitor's WinRT calls
/// already do (see MainWindow's TargetFramework comment).
///
/// The one WinRT member that genuinely does need package identity is the
/// *live push event*, NotificationChanged -- confirmed the hard way:
/// subscribing to it here threw COMException 0x80070490 ("element not
/// found") even though RequestAccessAsync had just returned Allowed. So this
/// polls GetNotificationsAsync on a timer instead, the same "no changed
/// event to hook, so ask on a schedule" shape every other monitor in this
/// app already uses for something with no push mechanism available to it
/// (WeatherMonitor's clock, GameMonitor's process list).
///
/// Only reacts to notifications not already seen -- StartAsync seeds the
/// seen-set from whatever's already sitting in Action Center, so the first
/// poll doesn't dump a burst of stale notifications onto Peemo's screen the
/// instant the checkbox is checked.
/// </summary>
public sealed class NotificationMonitor : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private UserNotificationListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private readonly HashSet<uint> _seenIds = new();

    /// <summary>Raised off a background thread, not the UI thread -- same convention as every other monitor here.</summary>
    public event Action<PcNotification>? NotificationReceived;

    /// <summary>Returns false if the user declined (or Windows otherwise refused) notification access -- never throws for that case.</summary>
    public async Task<bool> StartAsync()
    {
        UserNotificationListener listener = UserNotificationListener.Current;
        UserNotificationListenerAccessStatus access = await listener.RequestAccessAsync();
        if (access != UserNotificationListenerAccessStatus.Allowed)
        {
            return false;
        }

        _listener = listener;

        IReadOnlyList<UserNotification> initial = await listener.GetNotificationsAsync(NotificationKinds.Toast);
        foreach (UserNotification n in initial)
        {
            _seenIds.Add(n.Id);
        }

        _cts = new CancellationTokenSource();
        _pollTask = RunPollLoopAsync(_cts.Token);
        return true;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _pollTask = null;
        _listener = null;
        _seenIds.Clear();
    }

    public void Dispose() => Stop();

    private async Task RunPollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync();
            }
            catch
            {
                // A transient failure just tries again next tick -- same
                // "degrade gracefully, never take the loop down" convention
                // every background monitor in this app already follows.
            }

            try
            {
                await Task.Delay(PollInterval, token);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollOnceAsync()
    {
        UserNotificationListener? listener = _listener;
        if (listener == null)
        {
            return;
        }

        IReadOnlyList<UserNotification> current = await listener.GetNotificationsAsync(NotificationKinds.Toast);

        var currentIds = new HashSet<uint>();
        foreach (UserNotification n in current)
        {
            currentIds.Add(n.Id);
            if (_seenIds.Contains(n.Id))
            {
                continue;
            }

            string appName = n.AppInfo?.DisplayInfo?.DisplayName ?? "Notificação";
            List<string> parts = ExtractTextParts(n);
            if (parts.Count > 0)
            {
                string text = string.Join(" - ", parts);
                string title = parts[0];
                string? subtitle = parts.Count > 1 ? parts[1] : null;
                NotificationReceived?.Invoke(new PcNotification(appName, text, title, n.AppInfo?.AppUserModelId, subtitle));
            }
        }

        // Rebuilt from the current listing rather than just adding new ids
        // forever -- a dismissed notification's id is free to be reused by
        // Windows, and dropping stale ids keeps this from growing without
        // bound over a long-running session.
        _seenIds.Clear();
        foreach (uint id in currentIds)
        {
            _seenIds.Add(id);
        }
    }

    /// <summary>
    /// A toast's visible text elements in template order -- the first is
    /// the notification's title (ToastGeneric's own convention), the rest
    /// are body lines. Kept as separate parts (not pre-joined) so the
    /// caller can build a PcNotification with a real Title distinct from
    /// the full concatenated Text.
    /// </summary>
    private static List<string> ExtractTextParts(UserNotification notification)
    {
        var parts = new List<string>();
        NotificationBinding? binding = notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
        if (binding == null)
        {
            return parts;
        }

        foreach (AdaptiveNotificationText element in binding.GetTextElements())
        {
            if (!string.IsNullOrWhiteSpace(element.Text))
            {
                parts.Add(element.Text.Trim());
            }
        }
        return parts;
    }
}
