using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Data.Context;
using TaskFlow.Data.Entities;
using TaskFlow.Services.Projects;

namespace TaskFlow.Services.Tasks.Comments;

public sealed class TaskCommentService : ITaskCommentService
{
    private const int PageSize = 50;
    private const int MaxContentLength = 5000;
    private const int MaxMentions = 20;

    // "@" followed by an email address, for example @maria@example.com
    private static readonly Regex MentionPattern = new(
        @"@([A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,})",
        RegexOptions.Compiled);

    private readonly ApplicationDbContext _dbContext;

    public TaskCommentService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // ---------------------------------------------------------
    // Permissions (built on ProjectAccess, same shape as TaskService)
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

    private sealed record TaskInfo(Guid ProjectId, bool IsArchived);

    private async Task<TaskInfo?> GetTaskInfoAsync(Guid taskId)
    {
        return await _dbContext.Tasks
            .AsNoTracking()
            .Where(t => t.Id == taskId && !t.BoardColumn.Board.IsArchived)
            .Select(t => new TaskInfo(
                t.BoardColumn.Board.ProjectId, t.IsArchived))
            .SingleOrDefaultAsync();
    }

    // ---------------------------------------------------------
    // Read
    // ---------------------------------------------------------

    public async Task<TaskCommentList?> GetCommentsAsync(
        Guid taskId, string userId, int page = 1)
    {
        if (taskId == Guid.Empty || string.IsNullOrWhiteSpace(userId))
            return null;

        var info = await GetTaskInfoAsync(taskId);
        if (info is null) return null;

        var permissions = await GetPermissionsAsync(info.ProjectId, userId);
        if (!permissions.CanRead) return null;

        if (page < 1) page = 1;

        var query = _dbContext.TaskComments
            .AsNoTracking()
            .Where(c => c.TaskId == taskId);

        var total = await query.CountAsync();

        // Newest N comments, shown oldest first.
        var items = await query
            .OrderByDescending(c => c.CreatedAt)
            .Take(PageSize * page)
            .Select(c => new TaskCommentItem
            {
                Id = c.Id,
                TaskId = c.TaskId,
                ParentCommentId = c.ParentCommentId,
                UserId = c.UserId,
                AuthorName = c.User.FirstName + " " + c.User.LastName,
                Content = c.IsDeleted ? string.Empty : c.Content,
                CreatedAt = c.CreatedAt,
                UpdatedAt = c.UpdatedAt,
                IsDeleted = c.IsDeleted,
                IsEdited = !c.IsDeleted && c.UpdatedAt != null
            })
            .ToListAsync();

        items.Reverse();

        var canWrite = permissions.CanWrite && !info.IsArchived;

        foreach (var item in items)
        {
            var isAuthor = item.UserId == userId;

            item.CanEdit = !item.IsDeleted && isAuthor && canWrite;
            item.CanDelete = !item.IsDeleted && !info.IsArchived &&
                ((isAuthor && permissions.CanWrite) || permissions.CanManage);
        }

        return new TaskCommentList
        {
            Items = items,
            Page = page,
            TotalCount = total,
            HasOlder = total > PageSize * page,
            CanComment = canWrite
        };
    }

    // ---------------------------------------------------------
    // Add
    // ---------------------------------------------------------

    public async Task<TaskCommentResult> AddCommentAsync(
        Guid taskId, string userId, string content, Guid? parentCommentId)
    {
        if (taskId == Guid.Empty || string.IsNullOrWhiteSpace(userId))
            return TaskCommentResult.NotFound();

        var info = await GetTaskInfoAsync(taskId);
        if (info is null) return TaskCommentResult.NotFound();

        var permissions = await GetPermissionsAsync(info.ProjectId, userId);
        if (!permissions.CanRead) return TaskCommentResult.NotFound();

        if (!permissions.CanWrite)
            return TaskCommentResult.Forbidden(
                "You have view-only access to this project, so you cannot comment.");

        if (info.IsArchived)
            return TaskCommentResult.Forbidden(
                "This task is archived, so it is read only.");

        var error = ValidateContent(content, out var trimmed);
        if (error is not null) return TaskCommentResult.Invalid(error);

        if (parentCommentId.HasValue)
        {
            var parent = await _dbContext.TaskComments
                .AsNoTracking()
                .Where(c => c.Id == parentCommentId.Value)
                .Select(c => new { c.TaskId, c.ParentCommentId, c.IsDeleted })
                .SingleOrDefaultAsync();

            if (parent is null || parent.TaskId != taskId)
                return TaskCommentResult.Invalid(
                    "The comment you are replying to could not be found.");

            if (parent.ParentCommentId is not null)
                return TaskCommentResult.Invalid(
                    "You can only reply to a top-level comment.");

            if (parent.IsDeleted)
                return TaskCommentResult.Invalid(
                    "You cannot reply to a deleted comment.");
        }

        var comment = new TaskComment
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            UserId = userId,
            ParentCommentId = parentCommentId,
            Content = trimmed,
            CreatedAt = DateTime.UtcNow
        };

        foreach (var mentionedId in await ResolveMentionsAsync(
                     trimmed, info.ProjectId))
        {
            comment.Mentions.Add(new TaskCommentMention
            {
                MentionedUserId = mentionedId
            });
        }

        _dbContext.TaskComments.Add(comment);

        try
        {
            await _dbContext.SaveChangesAsync();
            return TaskCommentResult.Success(comment.Id);
        }
        catch (DbUpdateException)
        {
            return TaskCommentResult.Failed(
                "The comment could not be saved. Please try again.");
        }
    }

    // ---------------------------------------------------------
    // Update (author only)
    // ---------------------------------------------------------

    public async Task<TaskCommentResult> UpdateCommentAsync(
        Guid taskId, Guid commentId, string userId, string content)
    {
        if (taskId == Guid.Empty || commentId == Guid.Empty ||
            string.IsNullOrWhiteSpace(userId))
            return TaskCommentResult.NotFound();

        var info = await GetTaskInfoAsync(taskId);
        if (info is null) return TaskCommentResult.NotFound();

        var permissions = await GetPermissionsAsync(info.ProjectId, userId);
        if (!permissions.CanRead) return TaskCommentResult.NotFound();

        var comment = await _dbContext.TaskComments
            .Include(c => c.Mentions)
            .SingleOrDefaultAsync(c =>
                c.Id == commentId && c.TaskId == taskId);

        if (comment is null || comment.IsDeleted)
            return TaskCommentResult.NotFound();

        if (comment.UserId != userId)
            return TaskCommentResult.Forbidden(
                "You can only edit your own comments.");

        if (!permissions.CanWrite)
            return TaskCommentResult.Forbidden(
                "You have view-only access to this project, so you cannot edit comments.");

        if (info.IsArchived)
            return TaskCommentResult.Forbidden(
                "This task is archived, so it is read only.");

        var error = ValidateContent(content, out var trimmed);
        if (error is not null) return TaskCommentResult.Invalid(error);

        comment.Content = trimmed;
        comment.UpdatedAt = DateTime.UtcNow;

        // Editing recomputes the mentions.
        _dbContext.TaskCommentMentions.RemoveRange(comment.Mentions);

        foreach (var mentionedId in await ResolveMentionsAsync(
                     trimmed, info.ProjectId))
        {
            _dbContext.TaskCommentMentions.Add(new TaskCommentMention
            {
                TaskCommentId = comment.Id,
                MentionedUserId = mentionedId
            });
        }

        try
        {
            await _dbContext.SaveChangesAsync();
            return TaskCommentResult.Success(comment.Id);
        }
        catch (DbUpdateException)
        {
            return TaskCommentResult.Failed(
                "The comment could not be saved. Please try again.");
        }
    }

    // ---------------------------------------------------------
    // Delete (soft): the author or a Manager
    // ---------------------------------------------------------

    public async Task<TaskCommentResult> DeleteCommentAsync(
        Guid taskId, Guid commentId, string userId)
    {
        if (taskId == Guid.Empty || commentId == Guid.Empty ||
            string.IsNullOrWhiteSpace(userId))
            return TaskCommentResult.NotFound();

        var info = await GetTaskInfoAsync(taskId);
        if (info is null) return TaskCommentResult.NotFound();

        var permissions = await GetPermissionsAsync(info.ProjectId, userId);
        if (!permissions.CanRead) return TaskCommentResult.NotFound();

        var comment = await _dbContext.TaskComments
            .SingleOrDefaultAsync(c =>
                c.Id == commentId && c.TaskId == taskId);

        if (comment is null || comment.IsDeleted)
            return TaskCommentResult.NotFound();

        var isAuthor = comment.UserId == userId;

        if (!permissions.CanManage && !(isAuthor && permissions.CanWrite))
            return TaskCommentResult.Forbidden(
                "You do not have permission to delete this comment.");

        if (info.IsArchived)
            return TaskCommentResult.Forbidden(
                "This task is archived, so it is read only.");

        comment.IsDeleted = true;
        comment.DeletedAt = DateTime.UtcNow;
        comment.DeletedByUserId = userId;

        try
        {
            await _dbContext.SaveChangesAsync();
            return TaskCommentResult.Success(comment.Id);
        }
        catch (DbUpdateException)
        {
            return TaskCommentResult.Failed(
                "The comment could not be deleted. Please try again.");
        }
    }

    // ---------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------

    private static string? ValidateContent(string? content, out string trimmed)
    {
        trimmed = content?.Trim() ?? string.Empty;

        if (trimmed.Length == 0 || trimmed.Length > MaxContentLength)
            return "A comment must be between 1 and 5000 characters.";

        return null;
    }

    // Emails after "@" that belong to active users who can read the project.
    // Unknown, duplicate, inactive and no-access mentions are ignored.
    private async Task<List<string>> ResolveMentionsAsync(
        string content, Guid projectId)
    {
        var emails = MentionPattern.Matches(content)
            .Select(m => m.Groups[1].Value.ToUpperInvariant())
            .Distinct()
            .Take(MaxMentions)
            .ToList();

        if (emails.Count == 0) return new List<string>();

        var candidates = await _dbContext.Users
            .AsNoTracking()
            .Where(u => u.IsActive &&
                        u.NormalizedEmail != null &&
                        emails.Contains(u.NormalizedEmail))
            .Select(u => u.Id)
            .ToListAsync();

        var result = new List<string>();

        foreach (var id in candidates)
        {
            if (await _dbContext.Projects.ReadableBy(id)
                    .AnyAsync(p => p.Id == projectId))
            {
                result.Add(id);
            }
        }

        return result;
    }
}