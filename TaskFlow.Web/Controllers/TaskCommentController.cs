using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TaskFlow.Services.Tasks.Comments;
using TaskFlow.Services.Tasks.Models;

namespace TaskFlow.Web.Controllers;

[Authorize]
public class TaskCommentController : Controller
{
    private readonly ITaskCommentService _commentService;

    public TaskCommentController(ITaskCommentService commentService)
    {
        _commentService = commentService;
    }

    private string? GetCurrentUserId() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier);

    // POST: /TaskComment/Add
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(
        Guid taskId, string? content, Guid? parentCommentId)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Challenge();

        var result = await _commentService.AddCommentAsync(
            taskId, userId, content ?? string.Empty, parentCommentId);

        return Finish(result, taskId, "Comment added.");
    }

    // POST: /TaskComment/Edit
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(
        Guid taskId, Guid commentId, string? content)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Challenge();

        var result = await _commentService.UpdateCommentAsync(
            taskId, commentId, userId, content ?? string.Empty);

        return Finish(result, taskId, "Comment updated.");
    }

    // POST: /TaskComment/Delete
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid taskId, Guid commentId)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Challenge();

        var result = await _commentService.DeleteCommentAsync(
            taskId, commentId, userId);

        return Finish(result, taskId, "Comment deleted.");
    }

    private IActionResult Finish(
        TaskCommentResult result, Guid taskId, string successMessage)
    {
        // The task itself is not visible to this user: same 404 as the task page.
        if (result.Reason == TaskFailureReason.NotFound && !result.Succeeded)
            return NotFound();

        if (result.Succeeded)
            TempData["Success"] = successMessage;
        else
            TempData["Error"] = result.ErrorMessage ?? "The comment could not be saved.";

        return RedirectToAction("Details", "Task", new { id = taskId });
    }
}