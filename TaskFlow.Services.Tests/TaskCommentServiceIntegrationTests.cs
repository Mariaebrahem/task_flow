using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Data.Context;
using TaskFlow.Data.Entities;
using TaskFlow.Data.Enums;
using TaskFlow.Services.Tasks.Comments;
using TaskFlow.Services.Tasks.Models;
using AsyncTask = System.Threading.Tasks.Task;
using TaskEntity = TaskFlow.Data.Entities.Task;

namespace TaskFlow.Services.Tests;

/// <summary>
/// Role-by-role tests for task comments, using the same world as
/// TaskPermissionIntegrationTests: Project A (Manager, Member, Viewer,
/// inactive Member), a workspace Admin, an Outsider and a Stranger.
/// </summary>
[Collection(SqlServerIntegrationCollection.Name)]
public sealed class TaskCommentServiceIntegrationTests
{
    private readonly SqlServerIntegrationFixture fixture;

    public TaskCommentServiceIntegrationTests(SqlServerIntegrationFixture fixture)
    {
        this.fixture = fixture;
    }

    // ------------------------------------------------------------------
    // Adding comments
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask MembersManagersAndWorkspaceAdminsCanComment() => RunAsync(async s =>
    {
        foreach (var (userId, text) in new[]
        {
            (s.MemberId, "From member"),
            (s.ManagerId, "From manager"),
            (s.AdminId, "From admin")
        })
        {
            var result = await s.NewService().AddCommentAsync(s.TaskId, userId, text, null);

            Assert.True(result.Succeeded, result.ErrorMessage);
            Assert.NotNull(result.CommentId);

            var saved = await s.LoadCommentAsync(result.CommentId!.Value);
            Assert.Equal(userId, saved.UserId);
            Assert.Equal(s.TaskId, saved.TaskId);
            Assert.Equal(text, saved.Content);
        }
    });

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask ViewerCannotComment() => RunAsync(async s =>
    {
        var result = await s.NewService().AddCommentAsync(s.TaskId, s.ViewerId, "Viewer comment", null);

        Assert.False(result.Succeeded);
        Assert.Equal(TaskFailureReason.Forbidden, result.Reason);
        Assert.Equal(0, await s.CountCommentsAsync());
    });

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask OutsidersInactiveMembersAndStrangersGetNotFound() => RunAsync(async s =>
    {
        foreach (var userId in new[] { s.OutsiderId, s.InactiveId, s.StrangerId })
        {
            var result = await s.NewService().AddCommentAsync(s.TaskId, userId, "Nope", null);

            Assert.False(result.Succeeded);
            Assert.Equal(TaskFailureReason.NotFound, result.Reason);
        }

        var missingTask = await s.NewService().AddCommentAsync(Guid.NewGuid(), s.ManagerId, "Nope", null);

        Assert.Equal(TaskFailureReason.NotFound, missingTask.Reason);
        Assert.Equal(0, await s.CountCommentsAsync());
    });

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask ContentIsTrimmedAndLimited() => RunAsync(async s =>
    {
        var empty = await s.NewService().AddCommentAsync(s.TaskId, s.MemberId, "   ", null);
        Assert.Equal(TaskFailureReason.Invalid, empty.Reason);

        var tooLong = await s.NewService().AddCommentAsync(
            s.TaskId, s.MemberId, new string('a', 5001), null);
        Assert.Equal(TaskFailureReason.Invalid, tooLong.Reason);

        var exact = await s.NewService().AddCommentAsync(
            s.TaskId, s.MemberId, new string('a', 5000), null);
        Assert.True(exact.Succeeded, exact.ErrorMessage);

        var padded = await s.NewService().AddCommentAsync(s.TaskId, s.MemberId, "  hello  ", null);
        Assert.Equal("hello", (await s.LoadCommentAsync(padded.CommentId!.Value)).Content);

        Assert.Equal(2, await s.CountCommentsAsync());
    });

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask ArchivedTasksAreReadOnly() => RunAsync(async s =>
    {
        var rootId = (await s.NewService().AddCommentAsync(
            s.TaskId, s.MemberId, "Before archive", null)).CommentId!.Value;

        await s.ArchiveTaskAsync();

        var add = await s.NewService().AddCommentAsync(s.TaskId, s.ManagerId, "After archive", null);
        var edit = await s.NewService().UpdateCommentAsync(s.TaskId, rootId, s.MemberId, "Edited");
        var delete = await s.NewService().DeleteCommentAsync(s.TaskId, rootId, s.ManagerId);

        Assert.Equal(TaskFailureReason.Forbidden, add.Reason);
        Assert.Equal(TaskFailureReason.Forbidden, edit.Reason);
        Assert.Equal(TaskFailureReason.Forbidden, delete.Reason);
        Assert.Equal("Before archive", (await s.LoadCommentAsync(rootId)).Content);
    });

    // ------------------------------------------------------------------
    // Reading comments
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask EveryoneWhoCanReadSeesTheDiscussion() => RunAsync(async s =>
    {
        await s.NewService().AddCommentAsync(s.TaskId, s.MemberId, "First", null);
        await s.NewService().AddCommentAsync(s.TaskId, s.ManagerId, "Second", null);

        foreach (var userId in new[] { s.ViewerId, s.MemberId, s.ManagerId, s.AdminId })
        {
            var list = await s.NewService().GetCommentsAsync(s.TaskId, userId);

            Assert.NotNull(list);
            Assert.Equal(2, list.TotalCount);
            Assert.Equal(new[] { "First", "Second" }, list.Items.Select(i => i.Content).ToArray());
        }

        foreach (var userId in new[] { s.OutsiderId, s.InactiveId, s.StrangerId })
        {
            Assert.Null(await s.NewService().GetCommentsAsync(s.TaskId, userId));
        }
    });

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask CanCommentFlagIsFalseOnlyForViewers() => RunAsync(async s =>
    {
        Assert.False((await s.NewService().GetCommentsAsync(s.TaskId, s.ViewerId))!.CanComment);

        foreach (var userId in new[] { s.MemberId, s.ManagerId, s.AdminId })
        {
            Assert.True((await s.NewService().GetCommentsAsync(s.TaskId, userId))!.CanComment);
        }
    });

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask FlagsAgreeWithWhatEditAndDeleteActuallyAllow() => RunAsync(async s =>
    {
        var memberComment = (await s.NewService().AddCommentAsync(
            s.TaskId, s.MemberId, "By member", null)).CommentId!.Value;
        var managerComment = (await s.NewService().AddCommentAsync(
            s.TaskId, s.ManagerId, "By manager", null)).CommentId!.Value;

        foreach (var userId in new[] { s.ViewerId, s.MemberId, s.ManagerId, s.AdminId })
        {
            foreach (var commentId in new[] { memberComment, managerComment })
            {
                var item = (await s.NewService().GetCommentsAsync(s.TaskId, userId))!
                    .Items.Single(i => i.Id == commentId);

                var edit = await s.NewService().UpdateCommentAsync(s.TaskId, commentId, userId, "Parity");
                Assert.Equal(item.CanEdit, edit.Succeeded);

                var delete = await s.NewService().DeleteCommentAsync(s.TaskId, commentId, userId);
                Assert.Equal(item.CanDelete, delete.Succeeded);

                // Bring the comment back so the next user is checked
                // against the same starting state.
                await s.RestoreCommentAsync(commentId, "By someone");
            }
        }
    });

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask OlderCommentsArePagedFiftyAtATime() => RunAsync(async s =>
    {
        await s.SeedManyCommentsAsync(s.MemberId, 60);

        var first = await s.NewService().GetCommentsAsync(s.TaskId, s.ManagerId, 1);

        Assert.NotNull(first);
        Assert.Equal(50, first.Items.Count);
        Assert.Equal(60, first.TotalCount);
        Assert.True(first.HasOlder);

        var second = await s.NewService().GetCommentsAsync(s.TaskId, s.ManagerId, 2);

        Assert.Equal(60, second!.Items.Count);
        Assert.False(second.HasOlder);
    });

    // ------------------------------------------------------------------
    // Editing comments
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask OnlyTheAuthorCanEditAndItShowsAnEditedMark() => RunAsync(async s =>
    {
        var id = (await s.NewService().AddCommentAsync(
            s.TaskId, s.MemberId, "Original", null)).CommentId!.Value;

        foreach (var userId in new[] { s.ManagerId, s.AdminId, s.ViewerId })
        {
            var others = await s.NewService().UpdateCommentAsync(s.TaskId, id, userId, "Hijacked");

            Assert.Equal(TaskFailureReason.Forbidden, others.Reason);
        }

        Assert.Equal("Original", (await s.LoadCommentAsync(id)).Content);

        var own = await s.NewService().UpdateCommentAsync(s.TaskId, id, s.MemberId, "Changed");

        Assert.True(own.Succeeded, own.ErrorMessage);

        var item = (await s.NewService().GetCommentsAsync(s.TaskId, s.MemberId))!.Items.Single();

        Assert.Equal("Changed", item.Content);
        Assert.True(item.IsEdited);
    });

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask AViewerCannotEditEvenTheirOwnOldComment() => RunAsync(async s =>
    {
        var id = await s.SeedCommentAsync(s.ViewerId, "Written before demotion");

        var result = await s.NewService().UpdateCommentAsync(s.TaskId, id, s.ViewerId, "Edit");

        Assert.Equal(TaskFailureReason.Forbidden, result.Reason);
        Assert.Equal("Written before demotion", (await s.LoadCommentAsync(id)).Content);
    });

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask EditingValidatesAndHidesTheCommentFromOutsiders() => RunAsync(async s =>
    {
        var id = (await s.NewService().AddCommentAsync(
            s.TaskId, s.MemberId, "Original", null)).CommentId!.Value;

        var invalid = await s.NewService().UpdateCommentAsync(s.TaskId, id, s.MemberId, "  ");
        Assert.Equal(TaskFailureReason.Invalid, invalid.Reason);

        foreach (var userId in new[] { s.OutsiderId, s.InactiveId, s.StrangerId })
        {
            var result = await s.NewService().UpdateCommentAsync(s.TaskId, id, userId, "Nope");

            Assert.Equal(TaskFailureReason.NotFound, result.Reason);
        }

        Assert.Equal("Original", (await s.LoadCommentAsync(id)).Content);
    });

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask ACommentCannotBeAddressedThroughAnotherTask() => RunAsync(async s =>
    {
        var id = (await s.NewService().AddCommentAsync(
            s.TaskId, s.MemberId, "Original", null)).CommentId!.Value;

        var result = await s.NewService().UpdateCommentAsync(
            s.OtherTaskId, id, s.MemberId, "Wrong task");

        Assert.False(result.Succeeded);
        Assert.Equal("Original", (await s.LoadCommentAsync(id)).Content);
    });

    // ------------------------------------------------------------------
    // Deleting comments
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask AuthorOrManagerCanDeleteAndTheDeleteIsSoft() => RunAsync(async s =>
    {
        var byMember = (await s.NewService().AddCommentAsync(
            s.TaskId, s.MemberId, "Member's", null)).CommentId!.Value;

        var own = await s.NewService().DeleteCommentAsync(s.TaskId, byMember, s.MemberId);
        Assert.True(own.Succeeded, own.ErrorMessage);

        var saved = await s.LoadCommentAsync(byMember);
        Assert.True(saved.IsDeleted);
        Assert.NotNull(saved.DeletedAt);
        Assert.Equal(s.MemberId, saved.DeletedByUserId);

        foreach (var userId in new[] { s.ManagerId, s.AdminId })
        {
            var id = (await s.NewService().AddCommentAsync(
                s.TaskId, s.MemberId, "To be moderated", null)).CommentId!.Value;

            var moderated = await s.NewService().DeleteCommentAsync(s.TaskId, id, userId);

            Assert.True(moderated.Succeeded, moderated.ErrorMessage);
            Assert.Equal(userId, (await s.LoadCommentAsync(id)).DeletedByUserId);
        }
    });

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask MembersAndViewersCannotDeleteOtherPeoplesComments() => RunAsync(async s =>
    {
        var id = (await s.NewService().AddCommentAsync(
            s.TaskId, s.ManagerId, "Manager's", null)).CommentId!.Value;

        foreach (var userId in new[] { s.MemberId, s.ViewerId })
        {
            var result = await s.NewService().DeleteCommentAsync(s.TaskId, id, userId);

            Assert.Equal(TaskFailureReason.Forbidden, result.Reason);
        }

        foreach (var userId in new[] { s.OutsiderId, s.InactiveId, s.StrangerId })
        {
            var result = await s.NewService().DeleteCommentAsync(s.TaskId, id, userId);

            Assert.Equal(TaskFailureReason.NotFound, result.Reason);
        }

        Assert.False((await s.LoadCommentAsync(id)).IsDeleted);
    });

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask DeletedCommentsShowNoContentButKeepTheirReplies() => RunAsync(async s =>
    {
        var root = (await s.NewService().AddCommentAsync(
            s.TaskId, s.MemberId, "Secret text", null)).CommentId!.Value;
        await s.NewService().AddCommentAsync(s.TaskId, s.ManagerId, "A reply", root);

        await s.NewService().DeleteCommentAsync(s.TaskId, root, s.MemberId);

        var list = (await s.NewService().GetCommentsAsync(s.TaskId, s.ViewerId))!;
        var deleted = list.Items.Single(i => i.Id == root);
        var reply = list.Items.Single(i => i.ParentCommentId == root);

        Assert.True(deleted.IsDeleted);
        Assert.Equal(string.Empty, deleted.Content);
        Assert.False(deleted.CanEdit);
        Assert.False(deleted.CanDelete);
        Assert.Equal("A reply", reply.Content);

        var again = await s.NewService().DeleteCommentAsync(s.TaskId, root, s.MemberId);
        Assert.Equal(TaskFailureReason.NotFound, again.Reason);
    });

    // ------------------------------------------------------------------
    // Replies
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask RepliesAreOneLevelDeepOnTheSameTaskAndNotOnDeletedComments() => RunAsync(async s =>
    {
        var root = (await s.NewService().AddCommentAsync(
            s.TaskId, s.MemberId, "Root", null)).CommentId!.Value;

        var reply = await s.NewService().AddCommentAsync(s.TaskId, s.ManagerId, "Reply", root);
        Assert.True(reply.Succeeded, reply.ErrorMessage);

        var nested = await s.NewService().AddCommentAsync(
            s.TaskId, s.MemberId, "Reply to reply", reply.CommentId);
        Assert.Equal(TaskFailureReason.Invalid, nested.Reason);

        var otherTaskRoot = await s.SeedCommentAsync(s.ManagerId, "On other task", s.OtherTaskId);
        var crossTask = await s.NewService().AddCommentAsync(
            s.TaskId, s.MemberId, "Cross reply", otherTaskRoot);
        Assert.Equal(TaskFailureReason.Invalid, crossTask.Reason);

        var missingParent = await s.NewService().AddCommentAsync(
            s.TaskId, s.MemberId, "Orphan", Guid.NewGuid());
        Assert.Equal(TaskFailureReason.Invalid, missingParent.Reason);

        await s.NewService().DeleteCommentAsync(s.TaskId, root, s.MemberId);
        var onDeleted = await s.NewService().AddCommentAsync(s.TaskId, s.ManagerId, "Late", root);
        Assert.Equal(TaskFailureReason.Invalid, onDeleted.Reason);
    });

    // ------------------------------------------------------------------
    // Mentions
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask MentionsResolveOnlyActiveUsersWhoCanReadTheProject() => RunAsync(async s =>
    {
        var text =
            $"Hi @{s.EmailOf(s.ManagerId)} and @{s.EmailOf(s.ViewerId)} " +
            $"and @{s.EmailOf(s.ManagerId)} again, " +
            $"also @{s.EmailOf(s.OutsiderId)}, @{s.EmailOf(s.InactiveId)}, " +
            $"@{s.EmailOf(s.DeactivatedAccountId)} and @nobody@nowhere.test";

        var id = (await s.NewService().AddCommentAsync(
            s.TaskId, s.MemberId, text, null)).CommentId!.Value;

        var mentioned = await s.MentionedUserIdsAsync(id);

        Assert.Equal(
            new[] { s.ManagerId, s.ViewerId }.OrderBy(x => x).ToArray(),
            mentioned.OrderBy(x => x).ToArray());
    });

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask EditingACommentRecomputesItsMentions() => RunAsync(async s =>
    {
        var id = (await s.NewService().AddCommentAsync(
            s.TaskId, s.MemberId, $"Hi @{s.EmailOf(s.ManagerId)}", null)).CommentId!.Value;

        Assert.Equal(s.ManagerId, Assert.Single(await s.MentionedUserIdsAsync(id)));

        var edit = await s.NewService().UpdateCommentAsync(
            s.TaskId, id, s.MemberId, $"Now @{s.EmailOf(s.AdminId)}");

        Assert.True(edit.Succeeded, edit.ErrorMessage);
        Assert.Equal(s.AdminId, Assert.Single(await s.MentionedUserIdsAsync(id)));
    });

    [Fact]
    [Trait("Category", "Integration")]
    public AsyncTask AtMostTwentyMentionsAreStored() => RunAsync(async s =>
    {
        var emails = new List<string>();

        for (var i = 0; i < 25; i++)
        {
            emails.Add(await s.CreateProjectMemberEmailAsync());
        }

        var text = string.Join(" ", emails.Select(e => "@" + e));
        var id = (await s.NewService().AddCommentAsync(
            s.TaskId, s.MemberId, text, null)).CommentId!.Value;

        Assert.Equal(20, (await s.MentionedUserIdsAsync(id)).Count);
    });

    // ------------------------------------------------------------------
    // Test plumbing
    // ------------------------------------------------------------------

    private async AsyncTask RunAsync(Func<CommentScenario, AsyncTask> test)
    {
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();

        var transaction = await connection.BeginTransactionAsync();
        Exception? originalException = null;

        try
        {
            await using var scenario = await CommentScenario.CreateAsync(
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

    private sealed class CommentScenario : IAsyncDisposable
    {
        private readonly SqlServerIntegrationFixture fixture;
        private readonly SqlConnection connection;
        private readonly DbTransaction transaction;
        private readonly List<ApplicationDbContext> contexts = new();

        private Guid projectId;

        private CommentScenario(
            SqlServerIntegrationFixture fixture,
            SqlConnection connection,
            DbTransaction transaction)
        {
            this.fixture = fixture;
            this.connection = connection;
            this.transaction = transaction;
        }

        public string ManagerId { get; } = NewUserId("manager");
        public string MemberId { get; } = NewUserId("member");
        public string ViewerId { get; } = NewUserId("viewer");
        public string InactiveId { get; } = NewUserId("inactive");
        public string AdminId { get; } = NewUserId("admin");
        public string OutsiderId { get; } = NewUserId("outsider");
        public string StrangerId { get; } = NewUserId("stranger");

        // A project member whose account itself is switched off.
        public string DeactivatedAccountId { get; } = NewUserId("deactivated");

        public Guid TaskId { get; } = Guid.NewGuid();
        public Guid OtherTaskId { get; } = Guid.NewGuid();

        public static async System.Threading.Tasks.Task<CommentScenario> CreateAsync(
            SqlServerIntegrationFixture fixture,
            SqlConnection connection,
            DbTransaction transaction)
        {
            var scenario = new CommentScenario(fixture, connection, transaction);

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

        public TaskCommentService NewService() => new(NewContext());

        public string EmailOf(string userId) => $"{userId}@taskflow.test";

        public async System.Threading.Tasks.Task<TaskComment> LoadCommentAsync(Guid id)
        {
            return await NewContext().TaskComments
                .AsNoTracking()
                .SingleAsync(c => c.Id == id);
        }

        public async System.Threading.Tasks.Task<int> CountCommentsAsync()
        {
            return await NewContext().TaskComments
                .CountAsync(c => c.TaskId == TaskId);
        }

        public async System.Threading.Tasks.Task<List<string>> MentionedUserIdsAsync(Guid commentId)
        {
            return await NewContext().TaskCommentMentions
                .AsNoTracking()
                .Where(m => m.TaskCommentId == commentId)
                .Select(m => m.MentionedUserId)
                .ToListAsync();
        }

        public async System.Threading.Tasks.Task<Guid> SeedCommentAsync(
            string userId, string content, Guid? taskId = null)
        {
            var id = Guid.NewGuid();
            var context = NewContext();

            context.TaskComments.Add(new TaskComment
            {
                Id = id,
                TaskId = taskId ?? TaskId,
                UserId = userId,
                Content = content,
                CreatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();

            return id;
        }

        public async AsyncTask SeedManyCommentsAsync(string userId, int count)
        {
            var context = NewContext();
            var start = DateTime.UtcNow.AddMinutes(-count);

            for (var i = 0; i < count; i++)
            {
                context.TaskComments.Add(new TaskComment
                {
                    Id = Guid.NewGuid(),
                    TaskId = TaskId,
                    UserId = userId,
                    Content = $"Comment {i}",
                    CreatedAt = start.AddMinutes(i)
                });
            }

            await context.SaveChangesAsync();
        }

        public async AsyncTask RestoreCommentAsync(Guid id, string content)
        {
            var context = NewContext();
            var comment = await context.TaskComments.SingleAsync(c => c.Id == id);

            comment.IsDeleted = false;
            comment.DeletedAt = null;
            comment.DeletedByUserId = null;
            comment.Content = content;
            comment.UpdatedAt = null;
            await context.SaveChangesAsync();
        }

        public async AsyncTask ArchiveTaskAsync()
        {
            var context = NewContext();
            var task = await context.Tasks.SingleAsync(t => t.Id == TaskId);

            task.IsArchived = true;
            await context.SaveChangesAsync();
        }

        public async System.Threading.Tasks.Task<string> CreateProjectMemberEmailAsync()
        {
            var id = NewUserId("extra");
            var context = NewContext();

            context.AddRange(
                User(id),
                new ProjectMember
                {
                    ProjectId = projectId,
                    UserId = id,
                    Role = ProjectRole.Member,
                    IsActive = true
                });
            await context.SaveChangesAsync();

            return EmailOf(id);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var context in contexts)
            {
                await context.DisposeAsync();
            }
        }

        private async AsyncTask SeedAsync()
        {
            await using var context = fixture.CreateContext(connection);

            context.Database.UseTransaction(transaction);

            var workspaceId = Guid.NewGuid();
            var teamId = Guid.NewGuid();
            var otherTeamId = Guid.NewGuid();
            projectId = Guid.NewGuid();
            var otherProjectId = Guid.NewGuid();
            var boardId = Guid.NewGuid();
            var otherBoardId = Guid.NewGuid();
            var columnId = Guid.NewGuid();
            var otherColumnId = Guid.NewGuid();

            var deactivated = User(DeactivatedAccountId);
            deactivated.IsActive = false;

            context.AddRange(
                User(ManagerId),
                User(MemberId),
                User(ViewerId),
                User(InactiveId),
                User(AdminId),
                User(OutsiderId),
                User(StrangerId),
                deactivated,

                new Workspace { Id = workspaceId, Name = "Comment workspace" },
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
                Membership(projectId, ViewerId, ProjectRole.Viewer, true),
                Membership(projectId, InactiveId, ProjectRole.Member, false),
                Membership(projectId, DeactivatedAccountId, ProjectRole.Member, true),
                Membership(otherProjectId, OutsiderId, ProjectRole.Manager, true),

                SeedTask(TaskId, columnId, ManagerId, "Task A", 1),
                SeedTask(OtherTaskId, otherColumnId, OutsiderId, "Task B", 1));

            await context.SaveChangesAsync();
        }

        private static string NewUserId(string label) => $"cmt-{label}-{Guid.NewGuid():N}";

        private static ApplicationUser User(string id) => new()
        {
            Id = id,
            UserName = $"{id}@taskflow.test",
            NormalizedUserName = $"{id}@taskflow.test".ToUpperInvariant(),
            Email = $"{id}@taskflow.test",
            NormalizedEmail = $"{id}@taskflow.test".ToUpperInvariant(),
            FirstName = "Comment",
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