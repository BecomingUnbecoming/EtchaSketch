namespace EtchaSketch.Core.Privacy;

/// <summary>
/// An item that belongs to one person. Its Private and Secret parts are visible only to that person;
/// everyone else in the household sees at most its Household parts.
/// </summary>
public interface IOwned
{
    Guid OwnerId { get; }
}
