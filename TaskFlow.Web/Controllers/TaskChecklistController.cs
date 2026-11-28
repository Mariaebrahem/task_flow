using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TaskFlow.Services.Tasks.Checklists;
using TaskFlow.Services.Tasks.Models;

namespace TaskFlow.Web.Controllers;

[Authorize]
public class TaskChecklistController : Controller
{
    private readonly IChecklistService _checklistService;

    public TaskChecklistController(IChecklistService checklistService)
    {
        _checklistService = checklistService;
    }

    private string? GetCurrentUserId() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier);

    // POST: /TaskChecklist/Add
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(Guid taskId, string? title)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Challenge();

        var result = await _checklistService.AddAsync(
            taskId, userId, title ?? string.Empty);

        return Finish(result, taskId, "Checklist item added.");
    }

    // POST: /TaskChecklist/Rename
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Rename(Guid taskId, Guid itemId, string? title)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Challenge();

        var result = await _checklistService.RenameAsync(
            taskId, itemId, userId, title ?? string.Empty);

        return Finish(result, taskId, "Checklist item updated.");
    }

    // POST: /TaskChecklist/Toggle
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Toggle(Guid taskId, Guid itemId)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Challenge();

        var result = await _checklistService.ToggleAsync(taskId, itemId, userId);

        // No success message for a tick: the progress bar already shows it.
        return Finish(result, taskId, null);
    }

    // POST: /TaskChecklist/Remove
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Remove(Guid taskId, Guid itemId)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Challenge();

        var result = await _checklistService.RemoveAsync(taskId, itemId, userId);

        return Finish(result, taskId, "Checklist item removed.");
    }

    // POST: /TaskChecklist/Move   (direction: -1 up, 1 down)
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Move(Guid taskId, Guid itemId, int direction)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Challenge();

        var result = await _checklistService.MoveAsync(
            taskId, itemId, userId, direction);

        return Finish(result, taskId, null);
    }

    private IActionResult Finish(
        ChecklistResult result, Guid taskId, string? successMessage)
    {
        // Same 404 as the task page when the task is not visible.
        if (!result.Succeeded && result.Reason == TaskFailureReason.NotFound)
            return NotFound();

        if (result.Succeeded)
        {
            if (successMessage is not null)
                TempData["Success"] = successMessage;
        }
        else
        {
            TempData["Error"] = result.ErrorMessage
                ?? "The checklist could not be updated.";
        }

        return RedirectToAction(
            "Details", "Task", new { id = taskId }, fragment: "checklist");
    }
}