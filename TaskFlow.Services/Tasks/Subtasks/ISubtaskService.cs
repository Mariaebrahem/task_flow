namespace TaskFlow.Services.Tasks.Subtasks;

public interface ISubtaskService
{
    // Null when the task does not exist or the user cannot read it.
    Task<SubtaskList?> GetAsync(Guid taskId, string userId);

    Task<SubtaskResult> AddAsync(
        Guid taskId, string userId,
        string title, string? description, DateTime? dueDate);

    Task<SubtaskResult> UpdateAsync(
        Guid taskId, Guid subtaskId, string userId,
        string title, string? description, DateTime? dueDate);

    Task<SubtaskResult> ToggleAsync(Guid taskId, Guid subtaskId, string userId);

    Task<SubtaskResult> RemoveAsync(Guid taskId, Guid subtaskId, string userId);

    // direction: -1 moves up, +1 moves down.
    Task<SubtaskResult> MoveAsync(
        Guid taskId, Guid subtaskId, string userId, int direction);
}