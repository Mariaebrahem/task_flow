using Microsoft.EntityFrameworkCore;
using TaskFlow.Data.Context;
using TaskFlow.Data.Entities;
using TaskFlow.Services.Projects;
using AsyncTask = System.Threading.Tasks.Task;

namespace TaskFlow.Services.Tasks.Checklists;

public sealed class ChecklistService : IChecklistService
{
    private const int MaxItems = 100;
    private const int MaxTitleLength = 300;

    private readonly ApplicationDbContext _dbContext;

    public ChecklistService(ApplicationDbContext dbContext)
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

    // Same rule as TaskService.CanEditTask: a manager, or the creator
    // while that person can still contribute.
    private static bool CanEditStructure(
        ProjectPermissions p, TaskInfo info, string userId) =>
        p.CanManage || (p.CanWrite && info.CreatedByUserId == userId);

    private enum Access { Ok, NotFound, Forbidden }

    // Loads the task and checks the right level. Null permissions
    // mean the user cannot read the task at all.
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

    private const string ArchivedMessage =
        "This task is archived, so it is read only.";

    // ---------------------------------------------------------
    // Read
    // ---------------------------------------------------------

    public async Task<ChecklistDetails?> GetAsync(Guid taskId, string userId)
    {
        var (info, perms) = await LoadAsync(taskId, userId);
        if (info is null) return null;

        var items = await _dbContext.ChecklistItems
            .AsNoTracking()
            .Where(i => i.TaskId == taskId)
            .OrderBy(i => i.Position)
            .Select(i => new ChecklistItemView
            {
                Id = i.Id,
                Title = i.Title,
                IsCompleted = i.IsCompleted,
                Position = i.Position
            })
            .ToListAsync();

        var done = items.Count(i => i.IsCompleted);

        return new ChecklistDetails
        {
            TaskId = taskId,
            Items = items,
            Done = done,
            Total = items.Count,
            Percent = items.Count == 0
                ? 0
                : (int)Math.Round(done * 100.0 / items.Count),
            CanEditStructure = !info.IsArchived &&
                CanEditStructure(perms, info, userId),
            CanTick = !info.IsArchived && perms.CanWrite
        };
    }

    // ---------------------------------------------------------
    // Add
    // ---------------------------------------------------------

    public async Task<ChecklistResult> AddAsync(
        Guid taskId, string userId, string title)
    {
        var (info, perms) = await LoadAsync(taskId, userId);
        if (info is null) return ChecklistResult.NotFound();

        if (!CanEditStructure(perms, info, userId))
            return ChecklistResult.Forbidden(
                "You do not have permission to change this checklist.");

        if (info.IsArchived)
            return ChecklistResult.Forbidden(ArchivedMessage);

        var error = ValidateTitle(title, out var trimmed);
        if (error is not null) return ChecklistResult.Invalid(error);

        var count = await _dbContext.ChecklistItems
            .CountAsync(i => i.TaskId == taskId);

        if (count >= MaxItems)
            return ChecklistResult.Invalid(
                "A task can have at most 100 checklist items.");

        var next = await _dbContext.ChecklistItems
            .Where(i => i.TaskId == taskId)
            .Select(i => (int?)i.Position)
            .MaxAsync() ?? -1;

        _dbContext.ChecklistItems.Add(new ChecklistItem
        {
            TaskId = taskId,
            Title = trimmed,
            Position = next + 1,
            CreatedAt = DateTime.UtcNow
        });

        return await SaveAsync("The item could not be added. Please try again.");
    }

    // ---------------------------------------------------------
    // Rename
    // ---------------------------------------------------------

    public async Task<ChecklistResult> RenameAsync(
        Guid taskId, Guid itemId, string userId, string title)
    {
        var (info, perms) = await LoadAsync(taskId, userId);
        if (info is null) return ChecklistResult.NotFound();

        if (!CanEditStructure(perms, info, userId))
            return ChecklistResult.Forbidden(
                "You do not have permission to change this checklist.");

        if (info.IsArchived)
            return ChecklistResult.Forbidden(ArchivedMessage);

        var error = ValidateTitle(title, out var trimmed);
        if (error is not null) return ChecklistResult.Invalid(error);

        var item = await _dbContext.ChecklistItems
            .SingleOrDefaultAsync(i => i.Id == itemId && i.TaskId == taskId);

        if (item is null) return ChecklistResult.NotFound();

        item.Title = trimmed;
        item.UpdatedAt = DateTime.UtcNow;

        return await SaveAsync("The item could not be saved. Please try again.");
    }

    // ---------------------------------------------------------
    // Tick / untick (any contributor)
    // ---------------------------------------------------------

    public async Task<ChecklistResult> ToggleAsync(
        Guid taskId, Guid itemId, string userId)
    {
        var (info, perms) = await LoadAsync(taskId, userId);
        if (info is null) return ChecklistResult.NotFound();

        if (!perms.CanWrite)
            return ChecklistResult.Forbidden(
                "You have view-only access to this project, so you cannot tick items.");

        if (info.IsArchived)
            return ChecklistResult.Forbidden(ArchivedMessage);

        var item = await _dbContext.ChecklistItems
            .SingleOrDefaultAsync(i => i.Id == itemId && i.TaskId == taskId);

        if (item is null) return ChecklistResult.NotFound();

        var now = DateTime.UtcNow;

        item.IsCompleted = !item.IsCompleted;
        item.CompletedAt = item.IsCompleted ? now : null;
        item.UpdatedAt = now;

        return await SaveAsync("The item could not be saved. Please try again.");
    }

    // ---------------------------------------------------------
    // Remove (then close the gap in positions)
    // ---------------------------------------------------------

    public async Task<ChecklistResult> RemoveAsync(
        Guid taskId, Guid itemId, string userId)
    {
        var (info, perms) = await LoadAsync(taskId, userId);
        if (info is null) return ChecklistResult.NotFound();

        if (!CanEditStructure(perms, info, userId))
            return ChecklistResult.Forbidden(
                "You do not have permission to change this checklist.");

        if (info.IsArchived)
            return ChecklistResult.Forbidden(ArchivedMessage);

        var items = await _dbContext.ChecklistItems
            .Where(i => i.TaskId == taskId)
            .OrderBy(i => i.Position)
            .ToListAsync();

        var item = items.SingleOrDefault(i => i.Id == itemId);
        if (item is null) return ChecklistResult.NotFound();

        items.Remove(item);

        var ownsTransaction = _dbContext.Database.CurrentTransaction is null;
        await using var tx = ownsTransaction
            ? await _dbContext.Database.BeginTransactionAsync()
            : null;

        try
        {
            _dbContext.ChecklistItems.Remove(item);
            await _dbContext.SaveChangesAsync();

            await RenumberAsync(items);

            if (tx is not null) await tx.CommitAsync();
            return ChecklistResult.Success();
        }
        catch (DbUpdateException)
        {
            return ChecklistResult.Failed(
                "The item could not be removed. Please try again.");
        }
    }

    // ---------------------------------------------------------
    // Move up / down
    // ---------------------------------------------------------

    public async Task<ChecklistResult> MoveAsync(
        Guid taskId, Guid itemId, string userId, int direction)
    {
        var (info, perms) = await LoadAsync(taskId, userId);
        if (info is null) return ChecklistResult.NotFound();

        if (!CanEditStructure(perms, info, userId))
            return ChecklistResult.Forbidden(
                "You do not have permission to change this checklist.");

        if (info.IsArchived)
            return ChecklistResult.Forbidden(ArchivedMessage);

        if (direction != -1 && direction != 1)
            return ChecklistResult.Invalid("Invalid move direction.");

        var items = await _dbContext.ChecklistItems
            .Where(i => i.TaskId == taskId)
            .OrderBy(i => i.Position)
            .ToListAsync();

        var index = items.FindIndex(i => i.Id == itemId);
        if (index < 0) return ChecklistResult.NotFound();

        var target = index + direction;

        // Already at the edge: nothing to do.
        if (target < 0 || target >= items.Count)
            return ChecklistResult.Success();

        (items[index], items[target]) = (items[target], items[index]);

        var ownsTransaction = _dbContext.Database.CurrentTransaction is null;
        await using var tx = ownsTransaction
            ? await _dbContext.Database.BeginTransactionAsync()
            : null;

        try
        {
            await RenumberAsync(items);

            if (tx is not null) await tx.CommitAsync();
            return ChecklistResult.Success();
        }
        catch (DbUpdateException)
        {
            return ChecklistResult.Failed(
                "The item could not be moved. Please try again.");
        }
    }

    // ---------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------

    // Writes positions 0..n-1 in the given order. The unique index on
    // (TaskId, Position) forbids even a temporary duplicate, so every row
    // first moves to a unique negative slot, and only then to its final one.
    private async AsyncTask RenumberAsync(List<ChecklistItem> ordered)
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

    private async Task<ChecklistResult> SaveAsync(string failureMessage)
    {
        try
        {
            await _dbContext.SaveChangesAsync();
            return ChecklistResult.Success();
        }
        catch (DbUpdateException)
        {
            return ChecklistResult.Failed(failureMessage);
        }
    }

    private static string? ValidateTitle(string? title, out string trimmed)
    {
        trimmed = title?.Trim() ?? string.Empty;

        if (trimmed.Length == 0 || trimmed.Length > MaxTitleLength)
            return "A checklist item must be between 1 and 300 characters.";

        return null;
    }
}