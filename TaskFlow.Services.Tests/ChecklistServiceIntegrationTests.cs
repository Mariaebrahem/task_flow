using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Data.Context;
using TaskFlow.Data.Entities;
using TaskFlow.Data.Enums;
using TaskFlow.Services.Tasks.Checklists;
using TaskFlow.Services.Tasks.Models;
using AsyncTask = System.Threading.Tasks.Task;
using TaskEntity = TaskFlow.Data.Entities.Task;

namespace TaskFlow.Services.Tests;

[Collection(SqlServerIntegrationCollection.Name)]
public sealed class ChecklistServiceIntegrationTests
{
    private readonly SqlServerIntegrationFixture fixture;

    public ChecklistServiceIntegrationTests(SqlServerIntegrationFixture fixture)
    {
        this.fixture = fixture;
    }

    // ---------------- Adding ----------------

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask ManagerAndCreatorAndAdminCanAddButOthersCannot() => RunAsync(async s =>
    {
        // Task is created by the Member, so Member may edit structure.
        foreach (var userId in new[] { s.ManagerId, s.MemberId, s.AdminId })
        {
            var r = await s.NewService().AddAsync(s.TaskId, userId, "Item " + userId[..8]);
            Assert.True(r.Succeeded, r.ErrorMessage);
        }

        // A different Member (not creator) and a Viewer cannot.
        foreach (var userId in new[] { s.OtherMemberId, s.ViewerId })
        {
            var r = await s.NewService().AddAsync(s.TaskId, userId, "Nope");
            Assert.Equal(TaskFailureReason.Forbidden, r.Reason);
        }

        foreach (var userId in new[] { s.OutsiderId, s.InactiveId, s.StrangerId })
        {
            var r = await s.NewService().AddAsync(s.TaskId, userId, "Nope");
            Assert.Equal(TaskFailureReason.NotFound, r.Reason);
        }

        Assert.Equal(3, await s.CountItemsAsync());
    });

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask TitleIsTrimmedAndLimited() => RunAsync(async s =>
    {
        Assert.Equal(TaskFailureReason.Invalid,
            (await s.NewService().AddAsync(s.TaskId, s.ManagerId, "   ")).Reason);
        Assert.Equal(TaskFailureReason.Invalid,
            (await s.NewService().AddAsync(s.TaskId, s.ManagerId, new string('a', 301))).Reason);
        Assert.True((await s.NewService().AddAsync(s.TaskId, s.ManagerId, new string('a', 300))).Succeeded);
        Assert.True((await s.NewService().AddAsync(s.TaskId, s.ManagerId, "  padded  ")).Succeeded);

        var details = await s.NewService().GetAsync(s.TaskId, s.ManagerId);
        Assert.Equal("padded", details!.Items[1].Title);
    });

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask AtMostOneHundredItems() => RunAsync(async s =>
    {
        await s.SeedItemsAsync(100);

        var r = await s.NewService().AddAsync(s.TaskId, s.ManagerId, "One too many");

        Assert.Equal(TaskFailureReason.Invalid, r.Reason);
        Assert.Equal(100, await s.CountItemsAsync());
    });

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask ArchivedTasksAreReadOnly() => RunAsync(async s =>
    {
        await s.SeedItemsAsync(1);
        var id = await s.FirstItemIdAsync();
        await s.ArchiveTaskAsync();

        Assert.Equal(TaskFailureReason.Forbidden,
            (await s.NewService().AddAsync(s.TaskId, s.ManagerId, "x")).Reason);
        Assert.Equal(TaskFailureReason.Forbidden,
            (await s.NewService().RenameAsync(s.TaskId, id, s.ManagerId, "x")).Reason);
        Assert.Equal(TaskFailureReason.Forbidden,
            (await s.NewService().ToggleAsync(s.TaskId, id, s.ManagerId)).Reason);
        Assert.Equal(TaskFailureReason.Forbidden,
            (await s.NewService().RemoveAsync(s.TaskId, id, s.ManagerId)).Reason);
    });

    // ---------------- Reading ----------------

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask ReadersSeeTheListAndFlagsMatchTheirRole() => RunAsync(async s =>
    {
        await s.SeedItemsAsync(4, completed: 1);

        foreach (var userId in new[] { s.ViewerId, s.MemberId, s.OtherMemberId, s.ManagerId, s.AdminId })
        {
            var d = await s.NewService().GetAsync(s.TaskId, userId);
            Assert.NotNull(d);
            Assert.Equal(4, d.Total);
            Assert.Equal(1, d.Done);
            Assert.Equal(25, d.Percent);
        }

        var viewer = await s.NewService().GetAsync(s.TaskId, s.ViewerId);
        Assert.False(viewer!.CanTick);
        Assert.False(viewer.CanEditStructure);

        var other = await s.NewService().GetAsync(s.TaskId, s.OtherMemberId);
        Assert.True(other!.CanTick);
        Assert.False(other.CanEditStructure);

        var manager = await s.NewService().GetAsync(s.TaskId, s.ManagerId);
        Assert.True(manager!.CanTick);
        Assert.True(manager.CanEditStructure);

        foreach (var userId in new[] { s.OutsiderId, s.InactiveId, s.StrangerId })
            Assert.Null(await s.NewService().GetAsync(s.TaskId, userId));
    });

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask EmptyListHasZeroPercent() => RunAsync(async s =>
    {
        var d = await s.NewService().GetAsync(s.TaskId, s.ManagerId);

        Assert.Equal(0, d!.Total);
        Assert.Equal(0, d.Percent);
    });

    // ---------------- Ticking ----------------

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask AnyContributorCanTickButViewerCannot() => RunAsync(async s =>
    {
        await s.SeedItemsAsync(1);
        var id = await s.FirstItemIdAsync();

        var tick = await s.NewService().ToggleAsync(s.TaskId, id, s.OtherMemberId);
        Assert.True(tick.Succeeded, tick.ErrorMessage);

        var saved = await s.LoadItemAsync(id);
        Assert.True(saved.IsCompleted);
        Assert.NotNull(saved.CompletedAt);

        var untick = await s.NewService().ToggleAsync(s.TaskId, id, s.ManagerId);
        Assert.True(untick.Succeeded);
        saved = await s.LoadItemAsync(id);
        Assert.False(saved.IsCompleted);
        Assert.Null(saved.CompletedAt);

        Assert.Equal(TaskFailureReason.Forbidden,
            (await s.NewService().ToggleAsync(s.TaskId, id, s.ViewerId)).Reason);

        foreach (var userId in new[] { s.OutsiderId, s.InactiveId, s.StrangerId })
            Assert.Equal(TaskFailureReason.NotFound,
                (await s.NewService().ToggleAsync(s.TaskId, id, userId)).Reason);
    });

    // ---------------- Rename ----------------

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask RenameFollowsTheStructureRule() => RunAsync(async s =>
    {
        await s.SeedItemsAsync(1);
        var id = await s.FirstItemIdAsync();

        Assert.True((await s.NewService().RenameAsync(s.TaskId, id, s.ManagerId, "Renamed")).Succeeded);
        Assert.Equal("Renamed", (await s.LoadItemAsync(id)).Title);

        Assert.Equal(TaskFailureReason.Forbidden,
            (await s.NewService().RenameAsync(s.TaskId, id, s.ViewerId, "Hijack")).Reason);
        Assert.Equal(TaskFailureReason.Invalid,
            (await s.NewService().RenameAsync(s.TaskId, id, s.ManagerId, " ")).Reason);
        Assert.Equal(TaskFailureReason.NotFound,
            (await s.NewService().RenameAsync(s.TaskId, Guid.NewGuid(), s.ManagerId, "x")).Reason);
        Assert.Equal(TaskFailureReason.NotFound,
            (await s.NewService().RenameAsync(s.OtherTaskId, id, s.OutsiderId, "x")).Reason);
    });

    // ---------------- Remove / Move ----------------

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask RemoveClosesTheGapInPositions() => RunAsync(async s =>
    {
        await s.SeedItemsAsync(4);
        var ids = await s.OrderedIdsAsync();

        var r = await s.NewService().RemoveAsync(s.TaskId, ids[1], s.ManagerId);
        Assert.True(r.Succeeded, r.ErrorMessage);

        Assert.Equal(new[] { 0, 1, 2 }, await s.PositionsAsync());
        Assert.Equal(new[] { ids[0], ids[2], ids[3] }, await s.OrderedIdsAsync());

        Assert.Equal(TaskFailureReason.Forbidden,
            (await s.NewService().RemoveAsync(s.TaskId, ids[0], s.ViewerId)).Reason);
        Assert.Equal(3, await s.CountItemsAsync());
    });

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask MoveSwapsNeighboursAndKeepsPositionsContiguous() => RunAsync(async s =>
    {
        await s.SeedItemsAsync(3);
        var ids = await s.OrderedIdsAsync();

        Assert.True((await s.NewService().MoveAsync(s.TaskId, ids[2], s.ManagerId, -1)).Succeeded);
        Assert.Equal(new[] { ids[0], ids[2], ids[1] }, await s.OrderedIdsAsync());
        Assert.Equal(new[] { 0, 1, 2 }, await s.PositionsAsync());

        // Moving the first item up is a harmless no-op.
        var first = (await s.OrderedIdsAsync())[0];
        Assert.True((await s.NewService().MoveAsync(s.TaskId, first, s.ManagerId, -1)).Succeeded);
        Assert.Equal(new[] { 0, 1, 2 }, await s.PositionsAsync());

        Assert.Equal(TaskFailureReason.Invalid,
            (await s.NewService().MoveAsync(s.TaskId, first, s.ManagerId, 5)).Reason);
        Assert.Equal(TaskFailureReason.Forbidden,
            (await s.NewService().MoveAsync(s.TaskId, first, s.ViewerId, 1)).Reason);
    });

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask AnItemCannotBeAddressedThroughAnotherTask() => RunAsync(async s =>
    {
        await s.SeedItemsAsync(1);
        var id = await s.FirstItemIdAsync();

        var r = await s.NewService().RemoveAsync(s.OtherTaskId, id, s.OutsiderId);

        Assert.False(r.Succeeded);
        Assert.Equal(1, await s.CountItemsAsync());
    });

    // ---------------- Plumbing ----------------

    private async AsyncTask RunAsync(Func<ChecklistScenario, AsyncTask> test)
    {
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();

        var transaction = await connection.BeginTransactionAsync();
        Exception? originalException = null;

        try
        {
            await using var scenario = await ChecklistScenario.CreateAsync(
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

    private sealed class ChecklistScenario : IAsyncDisposable
    {
        private readonly SqlServerIntegrationFixture fixture;
        private readonly SqlConnection connection;
        private readonly DbTransaction transaction;
        private readonly List<ApplicationDbContext> contexts = new();

        private ChecklistScenario(
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
        public string OutsiderId { get; } = NewUserId("outsider");
        public string StrangerId { get; } = NewUserId("stranger");

        public Guid TaskId { get; } = Guid.NewGuid();
        public Guid OtherTaskId { get; } = Guid.NewGuid();

        public static async System.Threading.Tasks.Task<ChecklistScenario> CreateAsync(
            SqlServerIntegrationFixture fixture,
            SqlConnection connection,
            DbTransaction transaction)
        {
            var scenario = new ChecklistScenario(fixture, connection, transaction);
            await scenario.SeedAsync();
            return scenario;
        }

        public ApplicationDbContext NewContext()
        {
            var context = fixture.CreateContext(connection);
            context.Database.UseTransaction(transaction);
            contexts.Add(context);
            return context;
        }

        public ChecklistService NewService() => new(NewContext());

        public async System.Threading.Tasks.Task<int> CountItemsAsync() =>
            await NewContext().ChecklistItems.CountAsync(i => i.TaskId == TaskId);

        public async System.Threading.Tasks.Task<ChecklistItem> LoadItemAsync(Guid id) =>
            await NewContext().ChecklistItems.AsNoTracking().SingleAsync(i => i.Id == id);

        public async System.Threading.Tasks.Task<Guid> FirstItemIdAsync() =>
            (await OrderedIdsAsync())[0];

        public async System.Threading.Tasks.Task<Guid[]> OrderedIdsAsync() =>
            (await NewContext().ChecklistItems.AsNoTracking()
                .Where(i => i.TaskId == TaskId)
                .OrderBy(i => i.Position)
                .Select(i => i.Id)
                .ToListAsync()).ToArray();

        public async System.Threading.Tasks.Task<int[]> PositionsAsync() =>
            (await NewContext().ChecklistItems.AsNoTracking()
                .Where(i => i.TaskId == TaskId)
                .OrderBy(i => i.Position)
                .Select(i => i.Position)
                .ToListAsync()).ToArray();

        public async AsyncTask SeedItemsAsync(int count, int completed = 0)
        {
            var context = NewContext();

            for (var i = 0; i < count; i++)
            {
                context.ChecklistItems.Add(new ChecklistItem
                {
                    TaskId = TaskId,
                    Title = $"Item {i}",
                    Position = i,
                    IsCompleted = i < completed,
                    CompletedAt = i < completed ? DateTime.UtcNow : null
                });
            }

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

        private async AsyncTask SeedAsync()
        {
            await using var context = fixture.CreateContext(connection);
            context.Database.UseTransaction(transaction);

            var workspaceId = Guid.NewGuid();
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
                User(InactiveId), User(AdminId), User(OutsiderId), User(StrangerId),

                new Workspace { Id = workspaceId, Name = "Checklist workspace" },
                new Team { Id = teamId, WorkspaceId = workspaceId, Name = "Team A" },
                new Team { Id = otherTeamId, WorkspaceId = workspaceId, Name = "Team B" },

                new Project { Id = projectId, TeamId = teamId, Name = "Project A", Status = ProjectStatus.Active },
                new Project { Id = otherProjectId, TeamId = otherTeamId, Name = "Project B", Status = ProjectStatus.Active },

                new Board { Id = boardId, ProjectId = projectId, Name = "Board A", IsDefault = true },
                new Board { Id = otherBoardId, ProjectId = otherProjectId, Name = "Board B", IsDefault = true },

                new BoardColumn { Id = columnId, BoardId = boardId, Name = "To Do", Position = 1, IsDefault = true },
                new BoardColumn { Id = otherColumnId, BoardId = otherBoardId, Name = "To Do", Position = 1, IsDefault = true },

                new WorkspaceMember { WorkspaceId = workspaceId, UserId = AdminId, Role = WorkspaceRole.Admin },

                Membership(projectId, ManagerId, ProjectRole.Manager, true),
                Membership(projectId, MemberId, ProjectRole.Member, true),
                Membership(projectId, OtherMemberId, ProjectRole.Member, true),
                Membership(projectId, ViewerId, ProjectRole.Viewer, true),
                Membership(projectId, InactiveId, ProjectRole.Member, false),
                Membership(otherProjectId, OutsiderId, ProjectRole.Manager, true),

                SeedTask(TaskId, columnId, MemberId, "Task A", 1),
                SeedTask(OtherTaskId, otherColumnId, OutsiderId, "Task B", 1));

            await context.SaveChangesAsync();
        }

        private static string NewUserId(string label) => $"chk-{label}-{Guid.NewGuid():N}";

        private static ApplicationUser User(string id) => new()
        {
            Id = id,
            UserName = $"{id}@taskflow.test",
            NormalizedUserName = $"{id}@taskflow.test".ToUpperInvariant(),
            Email = $"{id}@taskflow.test",
            NormalizedEmail = $"{id}@taskflow.test".ToUpperInvariant(),
            FirstName = "Check",
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