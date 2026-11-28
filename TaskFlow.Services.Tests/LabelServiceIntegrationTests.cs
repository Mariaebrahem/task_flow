using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Data.Context;
using TaskFlow.Data.Entities;
using TaskFlow.Data.Enums;
using TaskFlow.Services.Tasks.Labels;
using TaskFlow.Services.Tasks.Models;
using AsyncTask = System.Threading.Tasks.Task;
using TaskEntity = TaskFlow.Data.Entities.Task;

namespace TaskFlow.Services.Tests;

[Collection(SqlServerIntegrationCollection.Name)]
public sealed class LabelServiceIntegrationTests
{
    private readonly SqlServerIntegrationFixture fixture;

    public LabelServiceIntegrationTests(SqlServerIntegrationFixture fixture)
    {
        this.fixture = fixture;
    }

    // ---------------- Create / Update ----------------

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask ManagersAndWorkspaceAdminsCanCreateOthersCannot() => RunAsync(async s =>
    {
        foreach (var (userId, name) in new[]
        {
            (s.ManagerId, "From manager"),
            (s.AdminId, "From admin"),
            (s.WorkspaceOwnerId, "From owner")
        })
        {
            var r = await s.NewService().CreateAsync(s.TaskId, userId, name, null, "#FF0000");
            Assert.True(r.Succeeded, r.ErrorMessage);
        }

        foreach (var userId in new[] { s.MemberId, s.ViewerId })
        {
            var r = await s.NewService().CreateAsync(s.TaskId, userId, "Nope", null, "#FF0000");
            Assert.Equal(TaskFailureReason.Forbidden, r.Reason);
        }

        foreach (var userId in new[] { s.OutsiderId, s.InactiveId, s.StrangerId })
        {
            var r = await s.NewService().CreateAsync(s.TaskId, userId, "Nope", null, "#FF0000");
            Assert.Equal(TaskFailureReason.NotFound, r.Reason);
        }

        Assert.Equal(3, await s.CountLabelsAsync());
    });

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask ValidationRulesAreEnforced() => RunAsync(async s =>
    {
        Assert.Equal(TaskFailureReason.Invalid,
            (await s.NewService().CreateAsync(s.TaskId, s.ManagerId, "  ", null, "#FF0000")).Reason);
        Assert.Equal(TaskFailureReason.Invalid,
            (await s.NewService().CreateAsync(s.TaskId, s.ManagerId, new string('a', 101), null, "#FF0000")).Reason);
        Assert.Equal(TaskFailureReason.Invalid,
            (await s.NewService().CreateAsync(s.TaskId, s.ManagerId, "ok", new string('d', 501), "#FF0000")).Reason);

        foreach (var bad in new[] { "red", "#FFF", "FF0000", "#GG0000", "#FF00000", "" })
        {
            Assert.Equal(TaskFailureReason.Invalid,
                (await s.NewService().CreateAsync(s.TaskId, s.ManagerId, "Color " + bad, null, bad)).Reason);
        }

        Assert.Equal(0, await s.CountLabelsAsync());
    });

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask NamesAreUniqueIgnoringCaseWithinAWorkspace() => RunAsync(async s =>
    {
        Assert.True((await s.NewService().CreateAsync(s.TaskId, s.ManagerId, "Bug", null, "#FF0000")).Succeeded);

        var dup = await s.NewService().CreateAsync(s.TaskId, s.ManagerId, "bUG", null, "#00FF00");
        Assert.Equal(TaskFailureReason.Invalid, dup.Reason);

        // The same name is fine in another workspace.
        var other = await s.NewService().CreateAsync(s.OtherTaskId, s.OutsiderId, "Bug", null, "#00FF00");
        Assert.True(other.Succeeded, other.ErrorMessage);

        Assert.Equal(1, await s.CountLabelsAsync());
    });

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask UpdateFollowsTheManageRule() => RunAsync(async s =>
    {
        var id = await s.SeedLabelAsync("Original");

        Assert.True((await s.NewService().UpdateAsync(
            s.TaskId, id, s.ManagerId, "Renamed", "desc", "#00ff00")).Succeeded);

        var saved = await s.LoadLabelAsync(id);
        Assert.Equal("Renamed", saved.Name);
        Assert.Equal("#00FF00", saved.Color);

        Assert.Equal(TaskFailureReason.Forbidden,
            (await s.NewService().UpdateAsync(s.TaskId, id, s.MemberId, "Hijack", null, "#000000")).Reason);
        Assert.Equal(TaskFailureReason.NotFound,
            (await s.NewService().UpdateAsync(s.TaskId, Guid.NewGuid(), s.ManagerId, "x", null, "#000000")).Reason);

        // Renaming to its own name is allowed; to another label's name is not.
        var second = await s.SeedLabelAsync("Second");
        Assert.True((await s.NewService().UpdateAsync(
            s.TaskId, second, s.ManagerId, "Second", null, "#000000")).Succeeded);
        Assert.Equal(TaskFailureReason.Invalid,
            (await s.NewService().UpdateAsync(s.TaskId, second, s.ManagerId, "renamed", null, "#000000")).Reason);
    });

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask ALabelOfAnotherWorkspaceCannotBeTouched() => RunAsync(async s =>
    {
        var foreign = await s.SeedLabelAsync("Foreign", s.OtherWorkspaceId);

        var update = await s.NewService().UpdateAsync(
            s.TaskId, foreign, s.ManagerId, "Hijack", null, "#000000");
        var delete = await s.NewService().DeleteAsync(s.TaskId, foreign, s.AdminId);

        Assert.Equal(TaskFailureReason.NotFound, update.Reason);
        Assert.Equal(TaskFailureReason.NotFound, delete.Reason);
        Assert.Equal("Foreign", (await s.LoadLabelAsync(foreign)).Name);
    });

    // ---------------- Delete ----------------

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask OnlyWorkspaceOwnerOrAdminCanDeleteAndItLeavesEveryTask() => RunAsync(async s =>
    {
        var id = await s.SeedLabelAsync("Doomed");
        await s.AttachAsync(s.TaskId, id);
        await s.AttachAsync(s.SecondTaskId, id);

        Assert.Equal(TaskFailureReason.Forbidden,
            (await s.NewService().DeleteAsync(s.TaskId, id, s.ManagerId)).Reason);
        Assert.Equal(TaskFailureReason.Forbidden,
            (await s.NewService().DeleteAsync(s.TaskId, id, s.MemberId)).Reason);
        Assert.Equal(1, await s.CountLabelsAsync());

        var r = await s.NewService().DeleteAsync(s.TaskId, id, s.AdminId);
        Assert.True(r.Succeeded, r.ErrorMessage);

        Assert.Equal(0, await s.CountLabelsAsync());
        Assert.Equal(0, await s.CountLinksAsync());
    });

    // ---------------- Setting labels on a task ----------------

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask OnlyThoseWhoCanEditTheTaskCanSetLabels() => RunAsync(async s =>
    {
        var a = await s.SeedLabelAsync("A");
        var b = await s.SeedLabelAsync("B");

        // Task creator is MemberId.
        foreach (var userId in new[] { s.MemberId, s.ManagerId, s.AdminId })
        {
            var r = await s.NewService().SetTaskLabelsAsync(s.TaskId, userId, new[] { a, b });
            Assert.True(r.Succeeded, r.ErrorMessage);
        }

        foreach (var userId in new[] { s.OtherMemberId, s.ViewerId })
        {
            var r = await s.NewService().SetTaskLabelsAsync(s.TaskId, userId, new[] { a });
            Assert.Equal(TaskFailureReason.Forbidden, r.Reason);
        }

        foreach (var userId in new[] { s.OutsiderId, s.InactiveId, s.StrangerId })
        {
            var r = await s.NewService().SetTaskLabelsAsync(s.TaskId, userId, new[] { a });
            Assert.Equal(TaskFailureReason.NotFound, r.Reason);
        }

        Assert.Equal(2, await s.CountLinksAsync());
    });

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask SettingReplacesTheSetWithoutDuplicates() => RunAsync(async s =>
    {
        var a = await s.SeedLabelAsync("A");
        var b = await s.SeedLabelAsync("B");
        var c = await s.SeedLabelAsync("C");

        Assert.True((await s.NewService().SetTaskLabelsAsync(
            s.TaskId, s.ManagerId, new[] { a, b, a })).Succeeded);
        Assert.Equal(2, await s.CountLinksAsync());

        Assert.True((await s.NewService().SetTaskLabelsAsync(
            s.TaskId, s.ManagerId, new[] { b, c })).Succeeded);

        var details = await s.NewService().GetForTaskAsync(s.TaskId, s.ManagerId);
        Assert.Equal(new HashSet<Guid> { b, c }, details!.SelectedIds.ToHashSet());

        Assert.True((await s.NewService().SetTaskLabelsAsync(
            s.TaskId, s.ManagerId, Array.Empty<Guid>())).Succeeded);
        Assert.Equal(0, await s.CountLinksAsync());

        // Removing labels from a task never deletes the labels themselves.
        Assert.Equal(3, await s.CountLabelsAsync());
    });

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask ALabelFromAnotherWorkspaceIsRejected() => RunAsync(async s =>
    {
        var own = await s.SeedLabelAsync("Own");
        var foreign = await s.SeedLabelAsync("Foreign", s.OtherWorkspaceId);

        var r = await s.NewService().SetTaskLabelsAsync(
            s.TaskId, s.ManagerId, new[] { own, foreign });

        Assert.Equal(TaskFailureReason.Invalid, r.Reason);
        Assert.Equal(0, await s.CountLinksAsync());

        var unknown = await s.NewService().SetTaskLabelsAsync(
            s.TaskId, s.ManagerId, new[] { Guid.NewGuid() });
        Assert.Equal(TaskFailureReason.Invalid, unknown.Reason);
    });

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask ArchivedTasksAreReadOnlyForLabels() => RunAsync(async s =>
    {
        var a = await s.SeedLabelAsync("A");
        await s.ArchiveTaskAsync();

        var r = await s.NewService().SetTaskLabelsAsync(s.TaskId, s.ManagerId, new[] { a });

        Assert.Equal(TaskFailureReason.Forbidden, r.Reason);

        var details = await s.NewService().GetForTaskAsync(s.TaskId, s.ManagerId);
        Assert.False(details!.CanSetLabels);
    });

    // ---------------- Reading ----------------

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask ReadersSeeWorkspaceLabelsAndFlagsMatchTheirRole() => RunAsync(async s =>
    {
        await s.SeedLabelAsync("Mine");
        await s.SeedLabelAsync("Foreign", s.OtherWorkspaceId);

        foreach (var userId in new[] { s.ViewerId, s.MemberId, s.OtherMemberId, s.ManagerId, s.AdminId })
        {
            var d = await s.NewService().GetForTaskAsync(s.TaskId, userId);
            Assert.NotNull(d);
            Assert.Equal("Mine", Assert.Single(d.Available).Name);
        }

        var viewer = await s.NewService().GetForTaskAsync(s.TaskId, s.ViewerId);
        Assert.False(viewer!.CanSetLabels);
        Assert.False(viewer.CanManageLabels);
        Assert.False(viewer.CanDeleteLabels);

        var member = await s.NewService().GetForTaskAsync(s.TaskId, s.MemberId);
        Assert.True(member!.CanSetLabels);
        Assert.False(member.CanManageLabels);

        var manager = await s.NewService().GetForTaskAsync(s.TaskId, s.ManagerId);
        Assert.True(manager!.CanSetLabels);
        Assert.True(manager.CanManageLabels);
        Assert.False(manager.CanDeleteLabels);

        var admin = await s.NewService().GetForTaskAsync(s.TaskId, s.AdminId);
        Assert.True(admin!.CanManageLabels);
        Assert.True(admin.CanDeleteLabels);

        foreach (var userId in new[] { s.OutsiderId, s.InactiveId, s.StrangerId })
            Assert.Null(await s.NewService().GetForTaskAsync(s.TaskId, userId));
    });

    [Fact]
    [Trait("Category", "Integration")]
    public void TextColorIsReadableOnTheBackground()
    {
        Assert.Equal("#FFFFFF", LabelColors.ReadableTextOn("#000000"));
        Assert.Equal("#000000", LabelColors.ReadableTextOn("#FFFFFF"));
        Assert.Equal("#000000", LabelColors.ReadableTextOn("#FFFF00"));
        Assert.Equal("#FFFFFF", LabelColors.ReadableTextOn("#0000FF"));
        Assert.Equal("#000000", LabelColors.ReadableTextOn("not-a-color"));
    }

    // ---------------- Plumbing ----------------

    private async AsyncTask RunAsync(Func<LabelScenario, AsyncTask> test)
    {
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();

        var transaction = await connection.BeginTransactionAsync();
        Exception? originalException = null;

        try
        {
            await using var scenario = await LabelScenario.CreateAsync(
                fixture, connection, transaction);

            await test(scenario);
        }
        catch (Exception exception)
        {
            originalException = exception;
            throw;
        }
        finally
        {
            await Stage3TestData.CleanupAsync(transaction, originalException);
        }
    }

    private sealed class LabelScenario : IAsyncDisposable
    {
        private readonly SqlServerIntegrationFixture fixture;
        private readonly SqlConnection connection;
        private readonly DbTransaction transaction;
        private readonly List<ApplicationDbContext> contexts = new();

        private LabelScenario(
            SqlServerIntegrationFixture fixture,
            SqlConnection connection,
            DbTransaction transaction)
        {
            this.fixture = fixture;
            this.connection = connection;
            this.transaction = transaction;
        }

        public string ManagerId { get; } = NewUserId("manager");
        public string MemberId { get; } = NewUserId("member");       // creator of the task
        public string OtherMemberId { get; } = NewUserId("other");
        public string ViewerId { get; } = NewUserId("viewer");
        public string InactiveId { get; } = NewUserId("inactive");
        public string AdminId { get; } = NewUserId("admin");
        public string WorkspaceOwnerId { get; } = NewUserId("wsowner");
        public string OutsiderId { get; } = NewUserId("outsider");   // manager in the other workspace
        public string StrangerId { get; } = NewUserId("stranger");

        public Guid WorkspaceId { get; } = Guid.NewGuid();
        public Guid OtherWorkspaceId { get; } = Guid.NewGuid();
        public Guid TaskId { get; } = Guid.NewGuid();
        public Guid SecondTaskId { get; } = Guid.NewGuid();
        public Guid OtherTaskId { get; } = Guid.NewGuid();

        public static async System.Threading.Tasks.Task<LabelScenario> CreateAsync(
            SqlServerIntegrationFixture fixture,
            SqlConnection connection,
            DbTransaction transaction)
        {
            var scenario = new LabelScenario(fixture, connection, transaction);
            await scenario.SeedWorldAsync();
            return scenario;
        }

        public ApplicationDbContext NewContext()
        {
            var context = fixture.CreateContext(connection);
            context.Database.UseTransaction(transaction);
            contexts.Add(context);
            return context;
        }

        public LabelService NewService() => new(NewContext());

        // Labels of the main workspace only.
        public async System.Threading.Tasks.Task<int> CountLabelsAsync() =>
            await NewContext().Labels.CountAsync(l => l.WorkspaceId == WorkspaceId);

        public async System.Threading.Tasks.Task<int> CountLinksAsync() =>
            await NewContext().TaskLabels.CountAsync(
                tl => tl.TaskId == TaskId || tl.TaskId == SecondTaskId);

        public async System.Threading.Tasks.Task<Label> LoadLabelAsync(Guid id) =>
            await NewContext().Labels.AsNoTracking().SingleAsync(l => l.Id == id);

        public async System.Threading.Tasks.Task<Guid> SeedLabelAsync(
            string name, Guid? workspaceId = null)
        {
            var id = Guid.NewGuid();
            var context = NewContext();

            context.Labels.Add(new Label
            {
                Id = id,
                WorkspaceId = workspaceId ?? WorkspaceId,
                Name = name,
                Color = "#808080",
                CreatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();

            return id;
        }

        public async AsyncTask AttachAsync(Guid taskId, Guid labelId)
        {
            var context = NewContext();

            context.TaskLabels.Add(new TaskLabel
            {
                Id = Guid.NewGuid(),
                TaskId = taskId,
                LabelId = labelId,
                AssignedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        public async AsyncTask ArchiveTaskAsync()
        {
            var context = NewContext();
            var task = await context.Tasks.SingleAsync(t => t.Id == TaskId);
            task.IsArchived = true;
            await context.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var context in contexts)
                await context.DisposeAsync();
        }

        private async AsyncTask SeedWorldAsync()
        {
            await using var context = fixture.CreateContext(connection);
            context.Database.UseTransaction(transaction);

            var teamId = Guid.NewGuid();
            var otherTeamId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var otherProjectId = Guid.NewGuid();
            var boardId = Guid.NewGuid();
            var otherBoardId = Guid.NewGuid();
            var columnId = Guid.NewGuid();
            var otherColumnId = Guid.NewGuid();

            context.AddRange(
                User(ManagerId), User(MemberId), User(OtherMemberId), User(ViewerId),
                User(InactiveId), User(AdminId), User(WorkspaceOwnerId),
                User(OutsiderId), User(StrangerId),

                new Workspace { Id = WorkspaceId, Name = "Label workspace" },
                new Workspace { Id = OtherWorkspaceId, Name = "Other label workspace" },
                new Team { Id = teamId, WorkspaceId = WorkspaceId, Name = "Team A" },
                new Team { Id = otherTeamId, WorkspaceId = OtherWorkspaceId, Name = "Team B" },

                new Project { Id = projectId, TeamId = teamId, Name = "Project A", Status = ProjectStatus.Active },
                new Project { Id = otherProjectId, TeamId = otherTeamId, Name = "Project B", Status = ProjectStatus.Active },

                new Board { Id = boardId, ProjectId = projectId, Name = "Board A", IsDefault = true },
                new Board { Id = otherBoardId, ProjectId = otherProjectId, Name = "Board B", IsDefault = true },

                new BoardColumn { Id = columnId, BoardId = boardId, Name = "To Do", Position = 1, IsDefault = true },
                new BoardColumn { Id = otherColumnId, BoardId = otherBoardId, Name = "To Do", Position = 1, IsDefault = true },

                new WorkspaceMember { WorkspaceId = WorkspaceId, UserId = AdminId, Role = WorkspaceRole.Admin },
                new WorkspaceMember { WorkspaceId = WorkspaceId, UserId = WorkspaceOwnerId, Role = WorkspaceRole.Owner },

                Membership(projectId, ManagerId, ProjectRole.Manager, true),
                Membership(projectId, MemberId, ProjectRole.Member, true),
                Membership(projectId, OtherMemberId, ProjectRole.Member, true),
                Membership(projectId, ViewerId, ProjectRole.Viewer, true),
                Membership(projectId, InactiveId, ProjectRole.Member, false),
                Membership(otherProjectId, OutsiderId, ProjectRole.Manager, true),

                SeedTask(TaskId, columnId, MemberId, "Task A", 1),
                SeedTask(SecondTaskId, columnId, ManagerId, "Task A2", 2),
                SeedTask(OtherTaskId, otherColumnId, OutsiderId, "Task B", 1));

            await context.SaveChangesAsync();
        }

        private static string NewUserId(string label) => $"lbl-{label}-{Guid.NewGuid():N}";

        private static ApplicationUser User(string id) => new()
        {
            Id = id,
            UserName = $"{id}@taskflow.test",
            NormalizedUserName = $"{id}@taskflow.test".ToUpperInvariant(),
            Email = $"{id}@taskflow.test",
            NormalizedEmail = $"{id}@taskflow.test".ToUpperInvariant(),
            FirstName = "Label",
            LastName = "Tester"
        };

        private static ProjectMember Membership(
            Guid projectId, string userId, ProjectRole role, bool isActive) => new()
            {
                ProjectId = projectId,
                UserId = userId,
                Role = role,
                IsActive = isActive
            };

        private static TaskEntity SeedTask(
            Guid id, Guid columnId, string createdBy, string title, int position) => new()
            {
                Id = id,
                BoardColumnId = columnId,
                CreatedByUserId = createdBy,
                Title = title,
                Priority = TaskPriority.Medium,
                Position = position
            };
    }
}