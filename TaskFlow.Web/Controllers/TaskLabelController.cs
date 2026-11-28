using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TaskFlow.Services.Tasks.Labels;
using TaskFlow.Services.Tasks.Models;

namespace TaskFlow.Web.Controllers;

[Authorize]
public class TaskLabelController : Controller
{
    private readonly ILabelService _labelService;

    public TaskLabelController(ILabelService labelService)
    {
        _labelService = labelService;
    }

    private string? GetCurrentUserId() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier);

    // Replaces the whole set of labels on the task.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Set(Guid taskId, List<Guid>? labelIds)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Challenge();

        var result = await _labelService.SetTaskLabelsAsync(
            taskId, userId, labelIds ?? new List<Guid>());

        return Finish(result, taskId, "Labels updated.");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        Guid taskId, string? name, string? description, string? color)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Challenge();

        var result = await _labelService.CreateAsync(
            taskId, userId, name ?? string.Empty, description, color ?? string.Empty);

        return Finish(result, taskId, "Label created.");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(
        Guid taskId, Guid labelId, string? name, string? description, string? color)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Challenge();

        var result = await _labelService.UpdateAsync(
            taskId, labelId, userId, name ?? string.Empty, description, color ?? string.Empty);

        return Finish(result, taskId, "Label updated.");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid taskId, Guid labelId)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Challenge();

        var result = await _labelService.DeleteAsync(taskId, labelId, userId);

        return Finish(result, taskId, "Label deleted from the workspace and from every task.");
    }

    private IActionResult Finish(LabelResult result, Guid taskId, string successMessage)
    {
        if (!result.Succeeded && result.Reason == TaskFailureReason.NotFound
            && result.ErrorMessage == "The label could not be found.")
        {
            // A missing label is a message; a hidden task is a 404 (handled below).
            TempData["Error"] = result.ErrorMessage;
            return RedirectToAction("Details", "Task", new { id = taskId }, fragment: "labels");
        }

        if (!result.Succeeded && result.Reason == TaskFailureReason.NotFound)
            return NotFound();

        if (result.Succeeded)
            TempData["Success"] = successMessage;
        else
            TempData["Error"] = result.ErrorMessage ?? "The labels could not be updated.";

        return RedirectToAction("Details", "Task", new { id = taskId }, fragment: "labels");
    }
}