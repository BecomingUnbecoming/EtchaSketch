using System.Globalization;
using EtchaSketch.Core.Privacy;
using EtchaSketch.Core.Tasks;

namespace EtchaSketch.Assistant;

/// <summary>A household task as the assistant's API shows it.</summary>
public sealed record AssistantTask(
    Guid Id, string Title, string? Due, string? DueTime, string Priority, string? Project, IReadOnlyList<string> Labels,
    string? AssignedTo, IReadOnlyList<string> WaitingOn);

/// <summary>A task to add. Everything but the title is optional; dates are 2026-10-21 and times 14:05.</summary>
public sealed record NewTask(string Title, string? Due = null, string? DueTime = null, string? Priority = null,
    string? Project = null, IReadOnlyList<string>? Labels = null, string? AssignTo = null);

public sealed record NewComment(string Text);

/// <summary>
/// The assistant's HTTP API: read the household's shared tasks, add one, finish one, comment on
/// one. Every request needs a caller's token (see <see cref="Callers"/>). Only shared tasks exist
/// here, and anything added is shared with the household.
/// </summary>
public static class AssistantApi
{
    public static WebApplication MapAssistantApi(this WebApplication app, Callers callers)
    {
        app.MapGet("/health", () => "ok");

        var api = app.MapGroup("/v1").AddEndpointFilter(async (context, next) =>
        {
            var header = context.HttpContext.Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..] : null;
            return callers.Who(token) is null ? Results.Unauthorized() : await next(context);
        });

        api.MapGet("/tasks", (AssistantData data, string? project, string? label, string? assignedTo) => data.Use(d =>
        {
            if (d.Household is not { } household)
            {
                return Results.Problem("The assistant hasn't joined a household yet.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var tasks = Shared(d)
                .Where(task => project is null || string.Equals(task.Project, project, StringComparison.OrdinalIgnoreCase))
                .Where(task => label is null || task.Labels.Contains(label, StringComparer.OrdinalIgnoreCase))
                .Select(task => Describe(d, task))
                .Where(task => assignedTo is null || string.Equals(task.AssignedTo, assignedTo, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return Results.Ok(tasks);
        }));

        api.MapPost("/tasks", (AssistantData data, NewTask body) => data.Use(d =>
        {
            if (d.Household is not { } household || d.Actor() is not { } actor)
            {
                return Results.Problem("Set Assistant__ActAs to someone in the household, so new tasks have an owner.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            try
            {
                var assignee = body.AssignTo is { } name
                    ? household.Members.FirstOrDefault(member => string.Equals(member.DisplayName, name, StringComparison.OrdinalIgnoreCase))
                      ?? throw new ArgumentException($"There's nobody called {name} in the household.")
                    : null;
                var task = d.Tasks.Add(new TaskItem
                {
                    HouseholdId = household.Id,
                    OwnerId = actor.Id,
                    Title = body.Title ?? "",
                    Due = body.Due is { } due ? DateOnly.ParseExact(due, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
                    DueTime = body.DueTime is { } time ? TimeOnly.ParseExact(time, "HH:mm", CultureInfo.InvariantCulture) : null,
                    Priority = body.Priority is { } priority ? Enum.Parse<TaskPriority>(priority, ignoreCase: true) : TaskPriority.Normal,
                    Project = body.Project,
                    Labels = body.Labels ?? [],
                    AssigneeId = assignee?.Id,
                    Tier = PrivacyTier.Household,
                });
                return Results.Created($"/v1/tasks/{task.Id}", Describe(d, task));
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException)
            {
                return Results.BadRequest(new { error = exception.Message.Split(" (Parameter")[0] });
            }
        }));

        api.MapPost("/tasks/{id:guid}/complete", (AssistantData data, Guid id, bool? evenIfBlocked) => data.Use(d =>
        {
            if (Find(d, id) is not { } task || d.Actor() is not { } actor)
            {
                return Results.NotFound();
            }

            try
            {
                d.Tasks.Complete(task.Id, actor.Id, d.Clock.GetLocalNow(), evenIfBlocked ?? false);
                return Results.NoContent();
            }
            catch (TaskBlockedException blocked)
            {
                return Results.Conflict(new { error = "It's waiting on other tasks.", waitingOn = blocked.Blockers.Select(b => b.Title) });
            }
        }));

        api.MapPost("/tasks/{id:guid}/comments", (AssistantData data, Guid id, NewComment body) => data.Use(d =>
        {
            if (Find(d, id) is not { } task || d.Actor() is not { } actor)
            {
                return Results.NotFound();
            }

            try
            {
                d.Tasks.AddComment(task.Id, actor.Id, body.Text ?? "", d.Clock.GetLocalNow());
                return Results.NoContent();
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message.Split(" (Parameter")[0] });
            }
        }));

        return app;
    }

    // Belt and braces: the assistant holds no private keys, and still only ever hands out shared tasks.
    private static IEnumerable<TaskItem> Shared(AssistantData data) =>
        data.Household is { } household
            ? data.Tasks.ChoreBoard(household.Id).SelectMany(column => column.Chores).Where(task => task.Tier <= PrivacyTier.Household)
            : [];

    private static TaskItem? Find(AssistantData data, Guid id) => Shared(data).FirstOrDefault(task => task.Id == id);

    private static AssistantTask Describe(AssistantData data, TaskItem task) => new(
        task.Id,
        task.Title,
        task.Due?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        task.DueTime?.ToString("HH:mm", CultureInfo.InvariantCulture),
        task.Priority.ToString().ToLowerInvariant(),
        task.Project,
        task.Labels,
        task.AssigneeId is { } id ? data.Household?.Members.FirstOrDefault(member => member.Id == id)?.DisplayName : null,
        data.Tasks.OpenBlockersOf(task.Id).Where(blocker => blocker.Tier <= PrivacyTier.Household).Select(blocker => blocker.Title).ToList());
}
