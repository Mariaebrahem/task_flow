using TaskFlow.Services.Tasks.Models;

namespace TaskFlow.Services.Tasks.Comments;

public class TaskCommentItem
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public Guid? ParentCommentId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string AuthorName { get; set; } = string.Empty;

    // Empty when the comment was deleted; the page shows "Comment deleted".
    public string Content { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public bool IsEdited { get; set; }
    public bool IsDeleted { get; set; }

    public bool CanEdit { get; set; }
    public bool CanDelete { get; set; }
}

public class TaskCommentList
{
    public IReadOnlyList<TaskCommentItem> Items { get; set; }
        = Array.Empty<TaskCommentItem>();

    public int Page { get; set; }
    public int TotalCount { get; set; }

    // True when older comments exist (the "Load older" button).
    public bool HasOlder { get; set; }

    // False for Viewers, so the page hides the posting form.
    public bool CanComment { get; set; }
}

public class TaskCommentResult
{
    public bool Succeeded { get; set; }
    public TaskFailureReason Reason { get; set; }
    public string? ErrorMessage { get; set; }
    public Guid? CommentId { get; set; }

    public static TaskCommentResult Success(Guid? commentId = null) =>
        new() { Succeeded = true, CommentId = commentId };

    public static TaskCommentResult NotFound() => new()
    {
        Reason = TaskFailureReason.NotFound,
        ErrorMessage = "The comment could not be found."
    };

    public static TaskCommentResult Forbidden(string message) => new()
    {
        Reason = TaskFailureReason.Forbidden,
        ErrorMessage = message
    };

    public static TaskCommentResult Invalid(string message) => new()
    {
        Reason = TaskFailureReason.Invalid,
        ErrorMessage = message
    };

    public static TaskCommentResult Failed(string message) => new()
    {
        Reason = TaskFailureReason.Failed,
        ErrorMessage = message
    };
}