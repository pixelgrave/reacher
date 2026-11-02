using System;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Reacher;

public enum Priority { Info, Attention, Urgent }

public enum ReachStatus { Queued, Showing, Acknowledged, Dismissed, Expired, Cancelled }

/// <summary>Body of POST /reach.</summary>
public sealed class ReachRequest
{
    public string? Message { get; set; }
    public string? Title { get; set; }
    public string? Priority { get; set; }
    public string? Source { get; set; }
    /// <summary>Seconds the bubble stays docked before it expires. 0 = until clicked.</summary>
    public double? Timeout { get; set; }
    /// <summary>Seconds the bubble follows the cursor before docking.</summary>
    public double? Chase { get; set; }
    /// <summary>URL or path opened when the bubble body is clicked.</summary>
    public string? Open { get; set; }
    /// <summary>Button labels; the clicked one is returned as the response.</summary>
    public string[]? Actions { get; set; }
    /// <summary>Seconds to hold the HTTP response open waiting for the user to respond.</summary>
    public double? Wait { get; set; }
    public bool? Sound { get; set; }
}

public sealed class ReachItem
{
    public required string Id { get; init; }
    public required string Message { get; init; }
    public string? Title { get; init; }
    public required string Source { get; init; }
    public Priority Priority { get; init; }
    public double TimeoutSeconds { get; init; }
    public double ChaseSeconds { get; init; }
    public string? Open { get; init; }
    public string[] Actions { get; init; } = [];
    public bool Sound { get; init; }

    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;
    public DateTimeOffset? ShownAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public ReachStatus Status { get; set; } = ReachStatus.Queued;
    public string? QueuedReason { get; set; }
    public string? Response { get; set; }

    [JsonIgnore]
    public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsFinal => Status is not (ReachStatus.Queued or ReachStatus.Showing);

    public static ReachItem From(ReachRequest r)
    {
        var priority = (r.Priority ?? "attention").Trim().ToLowerInvariant() switch
        {
            "info" or "low" => Priority.Info,
            "urgent" or "high" => Priority.Urgent,
            _ => Priority.Attention,
        };
        return new ReachItem
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Message = r.Message!.Trim(),
            Title = string.IsNullOrWhiteSpace(r.Title) ? null : r.Title.Trim(),
            Source = string.IsNullOrWhiteSpace(r.Source) ? "Reacher" : r.Source.Trim(),
            Priority = priority,
            TimeoutSeconds = Math.Max(0, r.Timeout ?? priority switch
            {
                Priority.Info => 20,
                Priority.Attention => 120,
                _ => 0,
            }),
            ChaseSeconds = Math.Clamp(r.Chase ?? priority switch
            {
                Priority.Info => 0,
                Priority.Attention => 3,
                _ => 5,
            }, 0, 30),
            Open = string.IsNullOrWhiteSpace(r.Open) ? null : r.Open.Trim(),
            Actions = (r.Actions ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).Take(4).ToArray(),
            Sound = r.Sound ?? false,
        };
    }

    public object ToDto(Presence presence) => new
    {
        id = Id,
        status = Status.ToString().ToLowerInvariant(),
        response = Response,
        queuedReason = Status == ReachStatus.Queued ? QueuedReason : null,
        priority = Priority.ToString().ToLowerInvariant(),
        source = Source,
        title = Title,
        message = Message,
        createdAt = CreatedAt,
        shownAt = ShownAt,
        closedAt = ClosedAt,
        presence = presence.ToDto(),
    };
}
