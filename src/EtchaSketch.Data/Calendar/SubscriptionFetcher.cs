namespace EtchaSketch.Data.Calendar;

/// <summary>What fetching subscribed calendars did.</summary>
/// <param name="Fetched">Calendars fetched afresh.</param>
/// <param name="Problems">One line for each that couldn't be: "School: the link didn't answer".</param>
public sealed record FetchResult(int Fetched, IReadOnlyList<string> Problems);

/// <summary>
/// Fetches the calendars one person follows, each at most every <see cref="MaxAge"/> unless asked
/// to, and keeps them on this device. A calendar that can't be fetched keeps what it had.
/// </summary>
public static class SubscriptionFetcher
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);

    public static async Task<FetchResult> RefreshAsync(SubscriptionRepository subscriptions, Guid personId, Func<Uri, Task<string>> fetch, DateTimeOffset now, bool force = false)
    {
        var fetched = 0;
        var problems = new List<string>();
        foreach (var subscription in subscriptions.VisibleTo(personId))
        {
            if (!force && subscriptions.Cached(subscription.Id) is { } cached && now - cached.FetchedAt < MaxAge)
            {
                continue;
            }

            try
            {
                var content = await fetch(subscription.Url);
                if (!content.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{subscription.Name}: that link doesn't give a calendar");
                    continue;
                }

                if (content.Length > SubscriptionRepository.MaxContentLength)
                {
                    problems.Add($"{subscription.Name}: the calendar is too big (over 5 MB)");
                    continue;
                }

                subscriptions.SaveFetched(subscription.Id, content, now);
                fetched++;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
            {
                problems.Add($"{subscription.Name}: couldn't fetch it ({exception.Message})");
            }
        }

        return new FetchResult(fetched, problems);
    }

    /// <summary>Fetches over HTTP, refusing anything over the size limit before reading it all.</summary>
    public static Func<Uri, Task<string>> Http(HttpClient http) => async url =>
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > SubscriptionRepository.MaxContentLength)
        {
            return "";
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var buffer = new char[SubscriptionRepository.MaxContentLength + 1];
        var read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
        return new string(buffer, 0, read);
    };
}
