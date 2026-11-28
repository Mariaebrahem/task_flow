using TaskFlow.Services.Tasks.Models;

namespace TaskFlow.Services.Tasks.Subtasks;

public class SubtaskView
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsCompleted { get; set; }
    public int Position { get; set; }
    public DateTime? DueDate { get; set; }
}

public class SubtaskList
{
    public Guid TaskId { get; set; }

    public IReadOnlyList<SubtaskView> Items { get; set; }
        = Array.Empty<SubtaskView>();

    public int Done { get; set; }
    public int Total { get; set; }

    // True when every subtask is done: the page shows a hint only,
    // the parent task is never completed automatically.
    public bool AllDone { get; set; }

    // Add, edit, remove and move.
    public bool CanEditStructure { get; set; }

    // Tick and untick.
    public bool CanTick { get; set; }
}

public class SubtaskResult
{
    public bool Succeeded { get; set; }
    public TaskFailureReason Reason { get; set; }
    public string? ErrorMessage { get; set; }

    public static SubtaskResult Success() => new() { Succeeded = true };

    public static SubtaskResult NotFound() => new()
    {
        Reason = TaskFailureReason.NotFound,
        ErrorMessage = "The subtask could not be found."
    };

    public static SubtaskResult Forbidden(string message) => new()
    {
        Reason = TaskFailureReason.Forbidden,
        ErrorMessage = message
    };

    public static SubtaskResult Invalid(string message) => new()
    {
        Reason = TaskFailureReason.Invalid,
        ErrorMessage = message
    };

    public static SubtaskResult Failed(string message) => new()
    {
        Reason = TaskFailureReason.Failed,
        ErrorMessage = message
    };
}