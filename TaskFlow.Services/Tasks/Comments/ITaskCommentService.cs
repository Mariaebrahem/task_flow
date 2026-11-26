namespace TaskFlow.Services.Tasks.Comments;

public interface ITaskCommentService
{
    // Null when the task does not exist or the user cannot read it.
    Task<TaskCommentList?> GetCommentsAsync(
        Guid taskId, string userId, int page = 1);

    Task<TaskCommentResult> AddCommentAsync(
        Guid taskId, string userId, string content, Guid? parentCommentId);

    Task<TaskCommentResult> UpdateCommentAsync(
        Guid taskId, Guid commentId, string userId, string content);

    Task<TaskCommentResult> DeleteCommentAsync(
        Guid taskId, Guid commentId, string userId);
}