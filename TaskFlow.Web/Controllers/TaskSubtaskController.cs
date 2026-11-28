using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TaskFlow.Services.Tasks.Models;
using TaskFlow.Services.Tasks.Subtasks;

namespace TaskFlow.Web.Controllers;

[Authorize]
public class TaskSubtaskController : Controller
{
    private readonly ISubtaskService _subtaskService;

    public TaskSubtaskController(ISubtaskService subtaskService)
    {
        _subtaskService = subtaskService;
    }

    private string? GetCurrentUserId() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier);

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(
        Guid taskId, string? title, string? description, DateTime? dueDate)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Challenge();

        var result = await _subtaskService.AddAsync(
            taskId, userId, title ?? string.Empty, description, dueDate);

        return Finish(result, taskId, "Subtask added.");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(
        Guid taskId, Guid subtaskId, string? title,
        string? description, DateTime? dueDate)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Challenge();

        var result = await _subtaskService.UpdateAsync(
            taskId, subtaskId, userId, title ?? string.Empty, description, dueDate);

        return Finish(result, taskId, "Subtask updated.");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Toggle(Guid taskId, Guid subtaskId)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Challenge();

        var result = await _subtaskService.ToggleAsync(taskId, subtaskId, userId);
        return Finish(result, taskId, null);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Remove(Guid taskId, Guid subtaskId)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Challenge();

        var result = await _subtaskService.RemoveAsync(taskId, subtaskId, userId);
        return Finish(result, taskId, "Subtask removed.");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Move(Guid taskId, Guid subtaskId, int direction)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Challenge();

        var result = await _subtaskService.MoveAsync(
            taskId, subtaskId, userId, direction);
        return Finish(result, taskId, null);
    }

    private IActionResult Finish(
        SubtaskResult result, Guid taskId, string? successMessage)
    {
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
                ?? "The subtasks could not be updated.";
        }

        return RedirectToAction(
            "Details", "Task", new { id = taskId }, fragment: "subtasks");
    }
}