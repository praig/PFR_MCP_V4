using Pfr.Core.Nios.Model;

namespace Pfr.Core.Query;

/// <summary>
/// Holds the loaded <see cref="SnapshotDatabase"/> of the grid being worked on, so Explore data and every report
/// template share one copy instead of each loading their own. Asking for a different grid lets the previous one go
/// (it is disposed once any query using it has finished); <see cref="Release"/> frees it when no grid is open.
/// </summary>
public sealed class SnapshotDatabaseCache : IDisposable
{
    private readonly object _gate = new();
    private GridInventory? _inventory;
    private Task<SnapshotDatabase>? _loading;

    /// <summary>
    /// The database for <paramref name="inventory"/>, loading it on a background thread the first time. A load that
    /// failed is tried again the next time.
    /// </summary>
    public Task<SnapshotDatabase> GetAsync(GridInventory inventory)
    {
        lock (_gate)
        {
            if (ReferenceEquals(inventory, _inventory) && _loading is { IsFaulted: false, IsCanceled: false }) return _loading;

            ReleaseLocked();
            _inventory = inventory;
            return _loading = Task.Run(() => SnapshotDatabase.Load(inventory));
        }
    }

    /// <summary>Lets go of the loaded data (when no grid is open, or it is no longer needed).</summary>
    public void Release()
    {
        lock (_gate) ReleaseLocked();
    }

    public void Dispose() => Release();

    private void ReleaseLocked()
    {
        var loading = _loading;
        _loading = null;
        _inventory = null;

        // Dispose it when loading has finished; Dispose itself waits for any running query.
        loading?.ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully) task.Result.Dispose();
        }, TaskScheduler.Default);
    }
}
