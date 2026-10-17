namespace EtchaSketch.UI.ViewModels;

/// <summary>A full-screen message for when the app can't continue, such as a missing database key.</summary>
public sealed class MessageViewModel(string heading, string detail) : ViewModelBase
{
    public string Heading { get; } = heading;

    public string Detail { get; } = detail;
}
