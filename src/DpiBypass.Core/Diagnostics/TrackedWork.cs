namespace DpiBypass.Core.Diagnostics;

/// <summary>
/// Keeps hold of background work so a shutdown can wait for it instead of only asking
/// it to stop.
/// </summary>
/// <remarks>
/// Cancelling a token means "please finish"; it says nothing about whether the work has
/// actually let go of the socket, the driver handle or the resolver it was using. Every
/// long lived task the service starts is registered here, so teardown can cancel, wait a
/// bounded time for the tasks to unwind, and only then dispose what they were holding.
/// The wait is bounded on purpose: a task wedged in a kernel call must delay shutdown by
/// seconds, not forever, and whatever is still running when the budget runs out has
/// already lost its right to write anything through the strategy coordinator.
/// </remarks>
public sealed class TrackedWork
{
    private readonly Lock _gate = new();
    private readonly HashSet<Task> _tasks = [];

    /// <summary>How many tasks are still running.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _tasks.Count;
            }
        }
    }

    /// <summary>Registers a task and removes it again when it finishes, however it finishes.</summary>
    public Task Track(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (task.IsCompleted)
        {
            Observe(task);
            return task;
        }

        lock (_gate)
        {
            _tasks.Add(task);
        }

        _ = task.ContinueWith(
            finished =>
            {
                lock (_gate)
                {
                    _tasks.Remove(finished);
                }

                Observe(finished);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return task;
    }

    /// <summary>
    /// Waits up to <paramref name="budget"/> for everything registered to finish.
    /// </summary>
    /// <returns>True when the queue drained inside the budget.</returns>
    public async Task<bool> DrainAsync(TimeSpan budget)
    {
        Task[] pending;
        lock (_gate)
        {
            pending = [.. _tasks];
        }

        if (pending.Length == 0)
        {
            return true;
        }

        try
        {
            await Task.WhenAll(pending).WaitAsync(budget).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (Exception)
        {
            // A task that failed has still finished, which is all this is waiting for.
            // Its own exception was already observed and logged where it was raised.
            return true;
        }
    }

    /// <summary>
    /// Reads a finished task's exception so it is never raised as unobserved.
    /// </summary>
    /// <remarks>
    /// An unobserved faulted task raises TaskScheduler.UnobservedTaskException on the
    /// finaliser thread. Nothing awaits these, so without this a background failure
    /// during shutdown would surface as a process level event with no context at all.
    /// </remarks>
    private static void Observe(Task task)
    {
        if (task.IsFaulted)
        {
            _ = task.Exception;
        }
    }
}
