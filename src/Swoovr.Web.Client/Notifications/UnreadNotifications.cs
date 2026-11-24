using Microsoft.AspNetCore.Components;
using Swoovr.Client;

namespace Swoovr.Web.Client.Notifications;

/// <summary>
/// Issue 472 (D397): the member's unread notification count, shared by every badge on the page (the Me tab and the
/// Notifications menu item) so they always agree. The count is the notification centre's own Unread (GET
/// /api/me/notifications), so it matches what /me/notifications lists. It refreshes while at least one badge is shown:
/// every 30 seconds (DEFAULT), on each in-app navigation, and at once when the Notifications page reads or marks items.
/// All WebAssembly components in the browser share this one instance.
/// </summary>
public sealed class UnreadNotifications(MemberClient members, NavigationManager navigation) : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim refreshing = new(1, 1);
    private CancellationTokenSource? polling;
    private int watchers;

    /// <summary>The last known count, or null before the first answer (badges stay hidden until then).</summary>
    public int? Count { get; private set; }

    public event Action? Changed;

    /// <summary>Sets the count from a centre this page already loaded (no extra request).</summary>
    public void Set(int unread)
    {
        if (Count == unread) return;
        Count = unread;
        Changed?.Invoke();
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!await refreshing.WaitAsync(0, cancellationToken)) return;
        try
        {
            var centre = await members.NotificationsAsync(cancellationToken);
            // A failed read (offline, signed out, refused) keeps the last count rather than flashing it away.
            if (centre.Value is { } value) Set(value.Unread);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or TaskCanceledException) { }
        finally { refreshing.Release(); }
    }

    /// <summary>A badge starts watching: the first one starts polling.</summary>
    public void Watch()
    {
        if (watchers++ > 0) return;
        polling = new CancellationTokenSource();
        navigation.LocationChanged += OnLocationChanged;
        _ = PollAsync(polling.Token);
    }

    /// <summary>A badge stops watching: the last one stops polling.</summary>
    public void Unwatch()
    {
        if (watchers == 0 || --watchers > 0) return;
        navigation.LocationChanged -= OnLocationChanged;
        polling?.Cancel();
        polling?.Dispose();
        polling = null;
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken);
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken)) await RefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException) { }
    }

    private void OnLocationChanged(object? sender, Microsoft.AspNetCore.Components.Routing.LocationChangedEventArgs e) => _ = RefreshAsync();

    public void Dispose()
    {
        navigation.LocationChanged -= OnLocationChanged;
        polling?.Cancel();
        polling?.Dispose();
        refreshing.Dispose();
    }
}
