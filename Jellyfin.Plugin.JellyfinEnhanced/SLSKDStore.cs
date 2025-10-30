using System.Collections.Concurrent;
using Jellyfin.Plugin.JellyfinEnhanced.Model;

public class SLSKDStore
{
    private readonly ConcurrentDictionary<string, SlskdRequest> _pendingSearches =
        new ConcurrentDictionary<string, SlskdRequest>();
    
    private readonly ConcurrentDictionary<string, SpotifyArtist> _pendingArtists =
        new ConcurrentDictionary<string, SpotifyArtist>();

    private TaskCompletionSource<bool> _searchTcs = new TaskCompletionSource<bool>();
    private TaskCompletionSource<bool> _artistTcs = new TaskCompletionSource<bool>();


    public async Task WaitForItemsAsync(CancellationToken cancellationToken)
    {
        var cancelledTask = Task.Delay(Timeout.Infinite, cancellationToken);
        Task completedTask = await Task.WhenAny(_searchTcs.Task, cancelledTask);
        if (completedTask == cancelledTask)
        {
            await completedTask;
        }
        await _searchTcs.Task;
    }

    public async Task WaitForArtistsAsync(CancellationToken cancellationToken)
    {
        var cancelledTask = Task.Delay(Timeout.Infinite, cancellationToken);
        Task completedTask = await Task.WhenAny(_artistTcs.Task, cancelledTask);
        if (completedTask == cancelledTask)
        {
            await completedTask;
        }
        await _artistTcs.Task;
    }
    
    public List<SpotifyArtist> GetAllArtists()
    {
        return _pendingArtists.Values.ToList();
    }

    public void AddArtist(SpotifyArtist request)
    {
        if (_pendingArtists.TryAdd(request.Id, request))
        {
            if (_artistTcs.Task.IsCompleted)
            {
                _artistTcs = new TaskCompletionSource<bool>();
            }
            _artistTcs.TrySetResult(true);
        }
    }

    public bool TryRemoveArtist(string artistId)
    {
        // 1. Try to remove the item safely
        bool removed = _pendingArtists.TryRemove(artistId, out _);

        if (removed)
        {
            // 2. Check if the collection is now empty
            if (_pendingArtists.IsEmpty)
            {
                var oldTcs = Interlocked.Exchange(ref _artistTcs, new TaskCompletionSource<bool>());
                oldTcs.TrySetResult(true); 
            }
        }

        return removed;
    }


    public List<SlskdRequest> GetAllRequests()
    {
        return _pendingSearches.Values.ToList();
    }

    public void AddRequest(SlskdRequest request)
    {
        if (_pendingSearches.TryAdd(request.SLSKDSearchId, request))
        {
            if (_searchTcs.Task.IsCompleted)
            {
                _searchTcs = new TaskCompletionSource<bool>();
            }
            _searchTcs.TrySetResult(true);
        }
    }

    public bool TryGetRequest(string searchId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SlskdRequest? request)
    {
        return _pendingSearches.TryGetValue(searchId, out request);
    }

    public ConcurrentDictionary<string, SlskdRequest> GetPendingSearches() => 
        _pendingSearches;
    
    public bool TryRemoveRequest(string searchId)
    {
        // 1. Try to remove the item safely
        bool removed = _pendingSearches.TryRemove(searchId, out _);

        if (removed)
        {
            // 2. Check if the collection is now empty
            if (_pendingSearches.IsEmpty)
            {
                var oldTcs = Interlocked.Exchange(ref _searchTcs, new TaskCompletionSource<bool>());
                oldTcs.TrySetResult(true); 
            }
        }

        return removed;
    }
}