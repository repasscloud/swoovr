using Microsoft.AspNetCore.Components;
using Swoovr.Client;
using Swoovr.Contracts;

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
    private long requested, applied;

    /// <summary>The newest centre the server has answered with, or null before the first answer.</summary>
    public NotificationCentre? Centre { get; private set; }

    /// <summary>The last known count, or null before the first answer (badges stay hidden until then).</summary>
    public int? Count => Centre?.Unread;

    /// <summary>Issue 540: connection requests waiting for an answer, from the same answer (no second poll).</summary>
    public int? PendingRequests => Centre?.PendingRequests;

    public event Action? Changed;

    /// <summary>
    /// Loads the centre and its count. Answers apply in the order they were asked for: one that started before the answer
    /// already shown (a poll overlapping a read) is dropped, so it can't put an older count back. A failed read (signed
    /// out, refused) keeps the last one and returns false.
    /// </summary>
    public async Task<bool> LoadAsync(CancellationToken cancellationToken = default)
    {
        var order = ++requested;
        if ((await members.NotificationsAsync(cancellationToken)).Value is not { } centre) return false;
        if (order < applied) return true; // a newer answer is already in Centre
        applied = order;
        var before = (Count, PendingRequests);
        Centre = centre;
        if ((Count, PendingRequests) != before) Changed?.Invoke();
        return true;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!await refreshing.WaitAsync(0, cancellationToken)) return;
        try
        {
            await LoadAsync(cancellationToken);
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
