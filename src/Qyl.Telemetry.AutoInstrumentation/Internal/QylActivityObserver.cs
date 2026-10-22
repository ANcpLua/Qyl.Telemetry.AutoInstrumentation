using System.Diagnostics;

namespace Qyl.Telemetry.AutoInstrumentation.Internal;

internal static class QylActivityObserver
{
    public static Task ObserveAsync(Task? task, Activity? activity)
    {
        if (activity is null || task is null)
        {
            activity?.Dispose();
            return task!;
        }

        return ObserveSlowAsync(task, activity);
    }

    public static Task<T> ObserveAsync<T>(Task<T>? task, Activity? activity)
    {
        if (activity is null || task is null)
        {
            activity?.Dispose();
            return task!;
        }

        return ObserveAsync(task, activity, completed: null);
    }

    private static async Task ObserveSlowAsync(Task task, Activity activity)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            QylActivityStatus.RecordFailure(activity, exception);
            throw;
        }
        finally
        {
            activity.Dispose();
        }
    }

    /// <summary>Awaits <paramref name="task"/>, records its failure on <paramref name="activity"/>, then
    /// runs <paramref name="completed"/> on either exit before the activity ends.</summary>
    internal static async Task<T> ObserveAsync<T>(Task<T> task, Activity? activity, Action? completed)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (activity is not null)
                QylActivityStatus.RecordFailure(activity, exception);
            throw;
        }
        finally
        {
            completed?.Invoke();
            activity?.Dispose();
        }
    }
}
