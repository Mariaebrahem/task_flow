using Microsoft.EntityFrameworkCore;
using TaskFlow.Data.Context;
using TaskFlow.Data.Entities;
using TaskFlow.Services.Projects;
using AsyncTask = System.Threading.Tasks.Task;

namespace TaskFlow.Services.Tasks.Subtasks;

public sealed class SubtaskService : ISubtaskService
{
    private const int MaxItems = 100;
    private const int MaxTitleLength = 200;
    private const int MaxDescriptionLength = 2000;

    private const string ArchivedMessage =
        "This task is archived, so it is read only.";

    private readonly ApplicationDbContext _dbContext;

    public SubtaskService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // ---------------------------------------------------------
    // Permissions (built on ProjectAccess)
    // ---------------------------------------------------------

    private readonly record struct ProjectPermissions(
        bool CanRead, bool CanWrite, bool CanManage);

    private async Task<ProjectPermissions> GetPermissionsAsync(
        Guid projectId, string userId)
    {
        if (await _dbContext.Projects.ManageableBy(userId)
                .AnyAsync(p => p.Id == projectId))
            return new(true, true, true);

        if (await _dbContext.Projects.WritableBy(userId)
                .AnyAsync(p => p.Id == projectId))
            return new(true, true, false);

        var canRead = await _dbContext.Projects.ReadableBy(userId)
            .AnyAsync(p => p.Id == projectId);

        return new(canRead, false, false);
    }

    private sealed record TaskInfo(
        Guid ProjectId, string CreatedByUserId, bool IsArchived);

    private async Task<TaskInfo?> GetTaskInfoAsync(Guid taskId)
    {
        return await _dbContext.Tasks
            .AsNoTracking()
            .Where(t => t.Id == taskId && !t.BoardColumn.Board.IsArchived)
            .Select(t => new TaskInfo(
                t.BoardColumn.Board.ProjectId,
                t.CreatedByUserId,
                t.IsArchived))
            .SingleOrDefaultAsync();
    }

    // Same rule as TaskService.CanEditTask.
    private static bool CanEditStructure(
        ProjectPermissions p, TaskInfo info, string userId) =>
        p.CanManage || (p.CanWrite && info.CreatedByUserId == userId);

    // Null info means the user cannot read the task at all.
    private async Task<(TaskInfo? Info, ProjectPermissions Perms)> LoadAsync(
        Guid taskId, string userId)
    {
        if (taskId == Guid.Empty || string.IsNullOrWhiteSpace(userId))
            return (null, default);

        var info = await GetTaskInfoAsync(taskId);
        if (info is null) return (null, default);

        var perms = await GetPermissionsAsync(info.ProjectId, userId);
        return perms.CanRead ? (info, perms) : (null, default);
    }

    // ---------------------------------------------------------
    // Read
    // ---------------------------------------------------------

    public async Task<SubtaskList?> GetAsync(Guid taskId, string userId)
    {
        var (info, perms) = await LoadAsync(taskId, userId);
        if (info is null) return null;

        var items = await _dbContext.Subtasks
            .AsNoTracking()
            .Where(s => s.TaskId == taskId)
            .OrderBy(s => s.Position)
            .Select(s => new SubtaskView
            {
                Id = s.Id,
                Title = s.Title,
                Description = s.Description,
                IsCompleted = s.IsCompleted,
                Position = s.Position,
                DueDate = s.DueDate
            })
            .ToListAsync();

        var done = items.Count(i => i.IsCompleted);

        return new SubtaskList
        {
            TaskId = taskId,
            Items = items,
            Done = done,
            Total = items.Count,
            AllDone = items.Count > 0 && done == items.Count,
            CanEditStructure = !info.IsArchived &&
                CanEditStructure(perms, info, userId),
            CanTick = !info.IsArchived && perms.CanWrite
        };
    }

    // ---------------------------------------------------------
    // Add
    // ---------------------------------------------------------

    public async Task<SubtaskResult> AddAsync(
        Guid taskId, string userId,
        string title, string? description, DateTime? dueDate)
    {
        var (info, perms) = await LoadAsync(taskId, userId);
        if (info is null) return SubtaskResult.NotFound();

        if (!CanEditStructure(perms, info, userId))
            return SubtaskResult.Forbidden(
                "You do not have permission to change the subtasks.");

        if (info.IsArchived)
            return SubtaskResult.Forbidden(ArchivedMessage);

        var error = Validate(title, description,
            out var trimmedTitle, out var normalizedDescription);
        if (error is not null) return SubtaskResult.Invalid(error);

        var count = await _dbContext.Subtasks
            .CountAsync(s => s.TaskId == taskId);

        if (count >= MaxItems)
            return SubtaskResult.Invalid(
                "A task can have at most 100 subtasks.");

        var next = await _dbContext.Subtasks
            .Where(s => s.TaskId == taskId)
            .Select(s => (int?)s.Position)
            .MaxAsync() ?? -1;

        _dbContext.Subtasks.Add(new Subtask
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            Title = trimmedTitle,
            Description = normalizedDescription,
            DueDate = dueDate,
            Position = next + 1,
            CreatedAt = DateTime.UtcNow
        });

        return await SaveAsync("The subtask could not be added. Please try again.");
    }

    // ---------------------------------------------------------
    // Update
    // ---------------------------------------------------------

    public async Task<SubtaskResult> UpdateAsync(
        Guid taskId, Guid subtaskId, string userId,
        string title, string? description, DateTime? dueDate)
    {
        var (info, perms) = await LoadAsync(taskId, userId);
        if (info is null) return SubtaskResult.NotFound();

        if (!CanEditStructure(perms, info, userId))
            return SubtaskResult.Forbidden(
                "You do not have permission to change the subtasks.");

        if (info.IsArchived)
            return SubtaskResult.Forbidden(ArchivedMessage);

        var error = Validate(title, description,
            out var trimmedTitle, out var normalizedDescription);
        if (error is not null) return SubtaskResult.Invalid(error);

        var subtask = await _dbContext.Subtasks
            .SingleOrDefaultAsync(s => s.Id == subtaskId && s.TaskId == taskId);

        if (subtask is null) return SubtaskResult.NotFound();

        subtask.Title = trimmedTitle;
        subtask.Description = normalizedDescription;
        subtask.DueDate = dueDate;
        subtask.UpdatedAt = DateTime.UtcNow;

        return await SaveAsync("The subtask could not be saved. Please try again.");
    }

    // ---------------------------------------------------------
    // Tick / untick (any contributor)
    // ---------------------------------------------------------

    public async Task<SubtaskResult> ToggleAsync(
        Guid taskId, Guid subtaskId, string userId)
    {
        var (info, perms) = await LoadAsync(taskId, userId);
        if (info is null) return SubtaskResult.NotFound();

        if (!perms.CanWrite)
            return SubtaskResult.Forbidden(
                "You have view-only access to this project, so you cannot tick subtasks.");

        if (info.IsArchived)
            return SubtaskResult.Forbidden(ArchivedMessage);

        var subtask = await _dbContext.Subtasks
            .SingleOrDefaultAsync(s => s.Id == subtaskId && s.TaskId == taskId);

        if (subtask is null) return SubtaskResult.NotFound();

        subtask.IsCompleted = !subtask.IsCompleted;
        subtask.UpdatedAt = DateTime.UtcNow;

        return await SaveAsync("The subtask could not be saved. Please try again.");
    }

    // ---------------------------------------------------------
    // Remove (then close the gap in positions)
    // ---------------------------------------------------------

    public async Task<SubtaskResult> RemoveAsync(
        Guid taskId, Guid subtaskId, string userId)
    {
        var (info, perms) = await LoadAsync(taskId, userId);
        if (info is null) return SubtaskResult.NotFound();

        if (!CanEditStructure(perms, info, userId))
            return SubtaskResult.Forbidden(
                "You do not have permission to change the subtasks.");

        if (info.IsArchived)
            return SubtaskResult.Forbidden(ArchivedMessage);

        var items = await _dbContext.Subtasks
            .Where(s => s.TaskId == taskId)
            .OrderBy(s => s.Position)
            .ToListAsync();

        var subtask = items.SingleOrDefault(s => s.Id == subtaskId);
        if (subtask is null) return SubtaskResult.NotFound();

        items.Remove(subtask);

        var ownsTransaction = _dbContext.Database.CurrentTransaction is null;
        await using var tx = ownsTransaction
            ? await _dbContext.Database.BeginTransactionAsync()
            : null;

        try
        {
            _dbContext.Subtasks.Remove(subtask);
            await _dbContext.SaveChangesAsync();

            await RenumberAsync(items);

            if (tx is not null) await tx.CommitAsync();
            return SubtaskResult.Success();
        }
        catch (DbUpdateException)
        {
            return SubtaskResult.Failed(
                "The subtask could not be removed. Please try again.");
        }
    }

    // ---------------------------------------------------------
    // Move up / down
    // ---------------------------------------------------------

    public async Task<SubtaskResult> MoveAsync(
        Guid taskId, Guid subtaskId, string userId, int direction)
    {
        var (info, perms) = await LoadAsync(taskId, userId);
        if (info is null) return SubtaskResult.NotFound();

        if (!CanEditStructure(perms, info, userId))
            return SubtaskResult.Forbidden(
                "You do not have permission to change the subtasks.");

        if (info.IsArchived)
            return SubtaskResult.Forbidden(ArchivedMessage);

        if (direction != -1 && direction != 1)
            return SubtaskResult.Invalid("Invalid move direction.");

        var items = await _dbContext.Subtasks
            .Where(s => s.TaskId == taskId)
            .OrderBy(s => s.Position)
            .ToListAsync();

        var index = items.FindIndex(s => s.Id == subtaskId);
        if (index < 0) return SubtaskResult.NotFound();

        var target = index + direction;

        // Already at the edge: nothing to do.
        if (target < 0 || target >= items.Count)
            return SubtaskResult.Success();

        (items[index], items[target]) = (items[target], items[index]);

        var ownsTransaction = _dbContext.Database.CurrentTransaction is null;
        await using var tx = ownsTransaction
            ? await _dbContext.Database.BeginTransactionAsync()
            : null;

        try
        {
            await RenumberAsync(items);

            if (tx is not null) await tx.CommitAsync();
            return SubtaskResult.Success();
        }
        catch (DbUpdateException)
        {
            return SubtaskResult.Failed(
                "The subtask could not be moved. Please try again.");
        }
    }

    // ---------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------

    // Writes positions 0..n-1 in the given order. The unique index on
    // (TaskId, Position) forbids even a temporary duplicate, so every row
    // first moves to a unique negative slot, then to its final one.
    private async AsyncTask RenumberAsync(List<Subtask> ordered)
    {
        var needsChange = false;

        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Position != i) needsChange = true;
        }

        if (!needsChange) return;

        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Position = -(i + 1);
        }

        await _dbContext.SaveChangesAsync();

        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Position = i;
        }

        await _dbContext.SaveChangesAsync();
    }

    private async Task<SubtaskResult> SaveAsync(string failureMessage)
    {
        try
        {
            await _dbContext.SaveChangesAsync();
            return SubtaskResult.Success();
        }
        catch (DbUpdateException)
        {
            return SubtaskResult.Failed(failureMessage);
        }
    }

    private static string? Validate(
        string? title, string? description,
        out string trimmedTitle, out string? normalizedDescription)
    {
        trimmedTitle = title?.Trim() ?? string.Empty;

        normalizedDescription = string.IsNullOrWhiteSpace(description)
            ? null
            : description.Trim();

        if (trimmedTitle.Length == 0 || trimmedTitle.Length > MaxTitleLength)
            return "A subtask title must be between 1 and 200 characters.";

        if (normalizedDescription?.Length > MaxDescriptionLength)
            return "A subtask description cannot exceed 2000 characters.";

        return null;
    }
}