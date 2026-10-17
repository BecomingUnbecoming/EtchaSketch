namespace EtchaSketch.Core.Tasks;

/// <summary>A record of someone finishing a task, kept so recurring chores have a history.</summary>
public sealed record TaskCompletion(Guid TaskId, Guid ProfileId, DateTimeOffset CompletedAt);
