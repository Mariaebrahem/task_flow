namespace TaskFlow.Services.Tasks.Labels;

public interface ILabelService
{
    // Null when the task does not exist or the user cannot read it.
    Task<TaskLabelsDetails?> GetForTaskAsync(Guid taskId, string userId);

    Task<LabelResult> CreateAsync(
        Guid taskId, string userId,
        string name, string? description, string color);

    Task<LabelResult> UpdateAsync(
        Guid taskId, Guid labelId, string userId,
        string name, string? description, string color);

    Task<LabelResult> DeleteAsync(Guid taskId, Guid labelId, string userId);

    // Replaces the whole set of labels on the task.
    Task<LabelResult> SetTaskLabelsAsync(
        Guid taskId, string userId, IEnumerable<Guid> labelIds);
}