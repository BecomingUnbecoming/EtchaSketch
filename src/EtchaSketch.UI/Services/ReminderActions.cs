using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace EtchaSketch.UI.Services;

/// <summary>
/// "Done" pressed on a reminder outside the app. The platform adds the task here; the home screen
/// completes it once someone is signed in (behind their PIN, if they have one), and only if they
/// can see that task.
/// </summary>
public static class ReminderActions
{
    private static readonly ConcurrentQueue<Guid> Completions = new();

    /// <summary>Raised on whichever thread asked, when a completion is waiting.</summary>
    public static event Action? Requested;

    public static void RequestComplete(Guid taskId)
    {
        Completions.Enqueue(taskId);
        Requested?.Invoke();
    }

    /// <summary>Takes every waiting completion, oldest first.</summary>
    public static IReadOnlyList<Guid> TakeCompletions()
    {
        var taken = new List<Guid>();
        while (Completions.TryDequeue(out var id))
        {
            taken.Add(id);
        }

        return taken;
    }
}
