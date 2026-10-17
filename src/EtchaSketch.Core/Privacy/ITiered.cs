namespace EtchaSketch.Core.Privacy;

/// <summary>An item that carries its own tier. A field can never be shown more widely than its item.</summary>
public interface ITiered
{
    PrivacyTier Tier { get; }
}
