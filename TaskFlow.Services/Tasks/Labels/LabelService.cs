using Microsoft.EntityFrameworkCore;
using TaskFlow.Data.Context;
using TaskFlow.Data.Entities;
using TaskFlow.Data.Enums;
using TaskFlow.Services.Projects;

namespace TaskFlow.Services.Tasks.Labels;

public sealed class LabelService : ILabelService
{
    private const int MaxNameLength = 100;
    private const int MaxDescriptionLength = 500;

    private const string ArchivedMessage =
        "This task is archived, so it is read only.";

    private readonly ApplicationDbContext _dbContext;

    public LabelService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // ---------------------------------------------------------
    // Context: the task, its project and its workspace
    // ---------------------------------------------------------

    private sealed record Context(
        Guid ProjectId,
        Guid WorkspaceId,
        string CreatedByUserId,
        bool IsArchived,
        bool CanRead,
        bool CanWrite,
        bool CanManageProject,
        bool IsWorkspaceOwner,
        bool IsWorkspaceAdmin);

    // Null when the task is missing or the user cannot read it.
    private async Task<Context?> LoadAsync(Guid taskId, string userId)
    {
        if (taskId == Guid.Empty || string.IsNullOrWhiteSpace(userId))
            return null;

        var info = await _dbContext.Tasks
            .AsNoTracking()
            .Where(t => t.Id == taskId && !t.BoardColumn.Board.IsArchived)
            .Select(t => new
            {
                ProjectId = t.BoardColumn.Board.ProjectId,
                WorkspaceId = t.BoardColumn.Board.Project.Team.WorkspaceId,
                t.CreatedByUserId,
                t.IsArchived
            })
            .SingleOrDefaultAsync();

        if (info is null) return null;

        var canRead = await _dbContext.Projects.ReadableBy(userId)
            .AnyAsync(p => p.Id == info.ProjectId);
        if (!canRead) return null;

        var canWrite = await _dbContext.Projects.WritableBy(userId)
            .AnyAsync(p => p.Id == info.ProjectId);
        var canManage = await _dbContext.Projects.ManageableBy(userId)
            .AnyAsync(p => p.Id == info.ProjectId);

        var wsRole = await _dbContext.WorkspaceMembers
            .AsNoTracking()
            .Where(m => m.WorkspaceId == info.WorkspaceId &&
                        m.UserId == userId && m.IsActive)
            .Select(m => (WorkspaceRole?)m.Role)
            .SingleOrDefaultAsync();

        return new Context(
            info.ProjectId, info.WorkspaceId, info.CreatedByUserId,
            info.IsArchived, true, canWrite, canManage,
            wsRole == WorkspaceRole.Owner,
            wsRole == WorkspaceRole.Admin);
    }

    // Same rule as TaskService.CanEditTask.
    private static bool CanEditTask(Context c, string userId) =>
        c.CanManageProject || (c.CanWrite && c.CreatedByUserId == userId);

    // Workspace Owner/Admin, or a Manager of a project in the workspace.
    private static bool CanManageLabels(Context c) =>
        c.IsWorkspaceOwner || c.IsWorkspaceAdmin || c.CanManageProject;

    private static bool CanDeleteLabels(Context c) =>
        c.IsWorkspaceOwner || c.IsWorkspaceAdmin;

    // ---------------------------------------------------------
    // Read
    // ---------------------------------------------------------

    public async Task<TaskLabelsDetails?> GetForTaskAsync(
        Guid taskId, string userId)
    {
        var c = await LoadAsync(taskId, userId);
        if (c is null) return null;

        var available = await _dbContext.Labels
            .AsNoTracking()
            .Where(l => l.WorkspaceId == c.WorkspaceId)
            .OrderBy(l => l.Name)
            .Select(l => new LabelView
            {
                Id = l.Id,
                Name = l.Name,
                Description = l.Description,
                Color = l.Color
            })
            .ToListAsync();

        var selected = await _dbContext.TaskLabels
            .AsNoTracking()
            .Where(tl => tl.TaskId == taskId)
            .Select(tl => tl.LabelId)
            .ToListAsync();

        return new TaskLabelsDetails
        {
            TaskId = taskId,
            Available = available,
            SelectedIds = selected.ToHashSet(),
            CanSetLabels = !c.IsArchived && CanEditTask(c, userId),
            CanManageLabels = CanManageLabels(c),
            CanDeleteLabels = CanDeleteLabels(c)
        };
    }

    // ---------------------------------------------------------
    // Create
    // ---------------------------------------------------------

    public async Task<LabelResult> CreateAsync(
        Guid taskId, string userId,
        string name, string? description, string color)
    {
        var c = await LoadAsync(taskId, userId);
        if (c is null) return LabelResult.NotFound();

        if (!CanManageLabels(c))
            return LabelResult.Forbidden(
                "You do not have permission to manage labels.");

        var error = Validate(name, description, color,
            out var trimmedName, out var normalizedDescription);
        if (error is not null) return LabelResult.Invalid(error);

        if (await NameExistsAsync(c.WorkspaceId, trimmedName, null))
            return LabelResult.Invalid(
                "A label with this name already exists in the workspace.");

        _dbContext.Labels.Add(new Label
        {
            Id = Guid.NewGuid(),
            WorkspaceId = c.WorkspaceId,
            Name = trimmedName,
            Description = normalizedDescription,
            Color = color.ToUpperInvariant(),
            CreatedAt = DateTime.UtcNow
        });

        return await SaveAsync("The label could not be created. Please try again.");
    }

    // ---------------------------------------------------------
    // Update
    // ---------------------------------------------------------

    public async Task<LabelResult> UpdateAsync(
        Guid taskId, Guid labelId, string userId,
        string name, string? description, string color)
    {
        var c = await LoadAsync(taskId, userId);
        if (c is null) return LabelResult.NotFound();

        if (!CanManageLabels(c))
            return LabelResult.Forbidden(
                "You do not have permission to manage labels.");

        var error = Validate(name, description, color,
            out var trimmedName, out var normalizedDescription);
        if (error is not null) return LabelResult.Invalid(error);

        var label = await _dbContext.Labels.SingleOrDefaultAsync(l =>
            l.Id == labelId && l.WorkspaceId == c.WorkspaceId);
        if (label is null) return LabelResult.NotFound();

        if (await NameExistsAsync(c.WorkspaceId, trimmedName, labelId))
            return LabelResult.Invalid(
                "A label with this name already exists in the workspace.");

        label.Name = trimmedName;
        label.Description = normalizedDescription;
        label.Color = color.ToUpperInvariant();

        return await SaveAsync("The label could not be saved. Please try again.");
    }

    // ---------------------------------------------------------
    // Delete: removes the label from every task first
    // ---------------------------------------------------------

    public async Task<LabelResult> DeleteAsync(
        Guid taskId, Guid labelId, string userId)
    {
        var c = await LoadAsync(taskId, userId);
        if (c is null) return LabelResult.NotFound();

        if (!CanDeleteLabels(c))
            return LabelResult.Forbidden(
                "Only a workspace Owner or Admin can delete labels.");

        var label = await _dbContext.Labels.SingleOrDefaultAsync(l =>
            l.Id == labelId && l.WorkspaceId == c.WorkspaceId);
        if (label is null) return LabelResult.NotFound();

        var links = await _dbContext.TaskLabels
            .Where(tl => tl.LabelId == labelId)
            .ToListAsync();

        var ownsTransaction = _dbContext.Database.CurrentTransaction is null;
        await using var tx = ownsTransaction
            ? await _dbContext.Database.BeginTransactionAsync()
            : null;

        try
        {
            _dbContext.TaskLabels.RemoveRange(links);
            _dbContext.Labels.Remove(label);
            await _dbContext.SaveChangesAsync();

            if (tx is not null) await tx.CommitAsync();
            return LabelResult.Success();
        }
        catch (DbUpdateException)
        {
            return LabelResult.Failed(
                "The label could not be deleted. Please try again.");
        }
    }

    // ---------------------------------------------------------
    // Set the labels of a task (replaces the whole set)
    // ---------------------------------------------------------

    public async Task<LabelResult> SetTaskLabelsAsync(
        Guid taskId, string userId, IEnumerable<Guid> labelIds)
    {
        var c = await LoadAsync(taskId, userId);
        if (c is null) return LabelResult.NotFound();

        if (!CanEditTask(c, userId))
            return LabelResult.Forbidden(
                "You do not have permission to change this task's labels.");

        if (c.IsArchived)
            return LabelResult.Forbidden(ArchivedMessage);

        var wanted = (labelIds ?? Array.Empty<Guid>()).Distinct().ToList();

        // Every label must belong to the task's own workspace.
        var valid = await _dbContext.Labels
            .Where(l => l.WorkspaceId == c.WorkspaceId &&
                        wanted.Contains(l.Id))
            .Select(l => l.Id)
            .ToListAsync();

        if (valid.Count != wanted.Count)
            return LabelResult.Invalid(
                "One or more labels do not belong to this workspace.");

        var current = await _dbContext.TaskLabels
            .Where(tl => tl.TaskId == taskId)
            .ToListAsync();

        _dbContext.TaskLabels.RemoveRange(
            current.Where(tl => !wanted.Contains(tl.LabelId)));

        var existing = current.Select(tl => tl.LabelId).ToHashSet();

        foreach (var id in wanted.Where(id => !existing.Contains(id)))
        {
            _dbContext.TaskLabels.Add(new TaskLabel
            {
                Id = Guid.NewGuid(),
                TaskId = taskId,
                LabelId = id,
                AssignedAt = DateTime.UtcNow
            });
        }

        return await SaveAsync("The labels could not be saved. Please try again.");
    }

    // ---------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------

    private async Task<bool> NameExistsAsync(
        Guid workspaceId, string name, Guid? exceptId)
    {
        var lower = name.ToLower();

        return await _dbContext.Labels.AnyAsync(l =>
            l.WorkspaceId == workspaceId &&
            l.Name.ToLower() == lower &&
            (exceptId == null || l.Id != exceptId));
    }

    private async Task<LabelResult> SaveAsync(string failureMessage)
    {
        try
        {
            await _dbContext.SaveChangesAsync();
            return LabelResult.Success();
        }
        catch (DbUpdateException)
        {
            return LabelResult.Failed(failureMessage);
        }
    }

    private static string? Validate(
        string? name, string? description, string? color,
        out string trimmedName, out string? normalizedDescription)
    {
        trimmedName = name?.Trim() ?? string.Empty;

        normalizedDescription = string.IsNullOrWhiteSpace(description)
            ? null
            : description.Trim();

        if (trimmedName.Length == 0 || trimmedName.Length > MaxNameLength)
            return "A label name must be between 1 and 100 characters.";

        if (normalizedDescription?.Length > MaxDescriptionLength)
            return "A label description cannot exceed 500 characters.";

        if (!LabelColors.IsValid(color))
            return "The color must look like #RRGGBB.";

        return null;
    }
}