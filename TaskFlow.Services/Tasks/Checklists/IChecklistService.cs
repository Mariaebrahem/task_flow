namespace TaskFlow.Services.Tasks.Checklists;

public interface IChecklistService
{
    // Null when the task does not exist or the user cannot read it.
    Task<ChecklistDetails?> GetAsync(Guid taskId, string userId);

    Task<ChecklistResult> AddAsync(Guid taskId, string userId, string title);

    Task<ChecklistResult> RenameAsync(
        Guid taskId, Guid itemId, string userId, string title);

    Task<ChecklistResult> ToggleAsync(Guid taskId, Guid itemId, string userId);

    Task<ChecklistResult> RemoveAsync(Guid taskId, Guid itemId, string userId);

    // direction: -1 moves the item up, +1 moves it down.
    Task<ChecklistResult> MoveAsync(
        Guid taskId, Guid itemId, string userId, int direction);
}