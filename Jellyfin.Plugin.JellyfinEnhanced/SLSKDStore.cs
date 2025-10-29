using System.Collections.Concurrent;

public class SLSKDStore
{
    private readonly ConcurrentDictionary<string, SLSKDRequest> _pendingSearches = 
        new ConcurrentDictionary<string, SLSKDRequest>();
    
    private TaskCompletionSource<bool> _tcs = new TaskCompletionSource<bool>();

    public async Task WaitForItemsAsync(CancellationToken cancellationToken)
    {
        var cancelledTask = Task.Delay(Timeout.Infinite, cancellationToken);
        Task completedTask = await Task.WhenAny(_tcs.Task, cancelledTask);
        if (completedTask == cancelledTask)
        {
            await completedTask; 
        }
        await _tcs.Task;
    }

    public List<SLSKDRequest> GetAllRequests()
    {
        return _pendingSearches.Values.ToList();
    }

    public void AddRequest(SLSKDRequest request)
    {
        if (_pendingSearches.TryAdd(request.SLSKDSearchId, request))
        {
            if (_tcs.Task.IsCompleted)
            {
                _tcs = new TaskCompletionSource<bool>();
            }
            _tcs.TrySetResult(true);
        }
    }
    public void UpdateRequest(string searchId, SLSKDRequest newRequestInfo)
    {
        _pendingSearches.AddOrUpdate(
            searchId, 
            newRequestInfo, 
            (key, existingRequest) => { return newRequestInfo; }
        );
    }
    public bool TryGetRequest(string searchId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SLSKDRequest? request)
    {
        return _pendingSearches.TryGetValue(searchId, out request);
    }

    public ConcurrentDictionary<string, SLSKDRequest> GetPendingSearches() => 
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
                var oldTcs = Interlocked.Exchange(ref _tcs, new TaskCompletionSource<bool>());
                oldTcs.TrySetResult(true); 
            }
        }

        return removed;
    }
}