namespace EtchaSketch.UI.ViewModels;

/// <summary>One notification as the feed shows it.</summary>
public sealed record NotificationRowViewModel(string Title, string? Body, string WhenText, bool IsUnread)
{
    public bool HasBody => !string.IsNullOrEmpty(Body);
}
