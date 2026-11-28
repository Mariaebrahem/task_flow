using TaskFlow.Services.Tasks.Models;

namespace TaskFlow.Services.Tasks.Labels;

public class LabelView
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Color { get; set; } = "#808080";

    // Black or white, whichever is readable on Color.
    public string TextColor => LabelColors.ReadableTextOn(Color);
}

public class TaskLabelsDetails
{
    public Guid TaskId { get; set; }

    // Every label of the task's workspace.
    public IReadOnlyList<LabelView> Available { get; set; }
        = Array.Empty<LabelView>();

    public IReadOnlySet<Guid> SelectedIds { get; set; }
        = new HashSet<Guid>();

    // Put and remove labels on the task.
    public bool CanSetLabels { get; set; }

    // Create and edit labels in the workspace.
    public bool CanManageLabels { get; set; }

    // Delete labels from the workspace (and from every task).
    public bool CanDeleteLabels { get; set; }
}

public class LabelResult
{
    public bool Succeeded { get; set; }
    public TaskFailureReason Reason { get; set; }
    public string? ErrorMessage { get; set; }

    public static LabelResult Success() => new() { Succeeded = true };

    public static LabelResult NotFound() => new()
    {
        Reason = TaskFailureReason.NotFound,
        ErrorMessage = "The label could not be found."
    };

    public static LabelResult Forbidden(string message) => new()
    {
        Reason = TaskFailureReason.Forbidden,
        ErrorMessage = message
    };

    public static LabelResult Invalid(string message) => new()
    {
        Reason = TaskFailureReason.Invalid,
        ErrorMessage = message
    };

    public static LabelResult Failed(string message) => new()
    {
        Reason = TaskFailureReason.Failed,
        ErrorMessage = message
    };
}

public static class LabelColors
{
    public static bool IsValid(string? color) =>
        color is { Length: 7 } && color[0] == '#' &&
        color.Skip(1).All(Uri.IsHexDigit);

    public static string ReadableTextOn(string color)
    {
        if (!IsValid(color)) return "#000000";

        var r = Convert.ToInt32(color.Substring(1, 2), 16);
        var g = Convert.ToInt32(color.Substring(3, 2), 16);
        var b = Convert.ToInt32(color.Substring(5, 2), 16);

        var luminance = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;

        return luminance > 0.55 ? "#000000" : "#FFFFFF";
    }
}