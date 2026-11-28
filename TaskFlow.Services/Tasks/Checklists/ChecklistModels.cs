using TaskFlow.Services.Tasks.Models;

namespace TaskFlow.Services.Tasks.Checklists;

public class ChecklistItemView
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public int Position { get; set; }
}

public class ChecklistDetails
{
    public Guid TaskId { get; set; }
    public IReadOnlyList<ChecklistItemView> Items { get; set; }
        = Array.Empty<ChecklistItemView>();

    public int Done { get; set; }
    public int Total { get; set; }

    // 0 to 100, 0 when the list is empty.
    public int Percent { get; set; }

    // Add, rename, remove and move.
    public bool CanEditStructure { get; set; }

    // Tick and untick.
    public bool CanTick { get; set; }
}

public class ChecklistResult
{
    public bool Succeeded { get; set; }
    public TaskFailureReason Reason { get; set; }
    public string? ErrorMessage { get; set; }

    public static ChecklistResult Success() => new() { Succeeded = true };

    public static ChecklistResult NotFound() => new()
    {
        Reason = TaskFailureReason.NotFound,
        ErrorMessage = "The checklist item could not be found."
    };

    public static ChecklistResult Forbidden(string message) => new()
    {
        Reason = TaskFailureReason.Forbidden,
        ErrorMessage = message
    };

    public static ChecklistResult Invalid(string message) => new()
    {
        Reason = TaskFailureReason.Invalid,
        ErrorMessage = message
    };

    public static ChecklistResult Failed(string message) => new()
    {
        Reason = TaskFailureReason.Failed,
        ErrorMessage = message
    };
}