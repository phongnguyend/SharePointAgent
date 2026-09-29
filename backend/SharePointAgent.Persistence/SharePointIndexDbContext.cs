using Microsoft.EntityFrameworkCore;
using SharePointAgent.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;

namespace SharePointAgent.Persistence;

/// <summary>
/// Every table the application owns: the worker's delta checkpoints and indexed-file records, and the
/// chat assistant's conversations and messages. The model is the source of truth for the schema — it is
/// what <c>dotnet ef migrations add</c> reads — so table and column names are fixed here rather than
/// configured, and a change to them is a migration.
/// </summary>
public sealed class SharePointIndexDbContext(DbContextOptions<SharePointIndexDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    /// <summary>Identifier columns that carry a Microsoft Graph drive or item ID.</summary>
    private const int IdentifierLength = 200;

    /// <summary>
    /// Columns holding a comma-separated list of tool, skill, or script names from one model response.
    /// Long enough for the several calls a response can ask for at once; the writer truncates past it.
    /// </summary>
    public const int ToolListLength = 1000;

    /// <summary>
    /// The longest workspace rule text. Every turn in the workspace carries it in the system prompt,
    /// so it is capped rather than left to the column's maximum.
    /// </summary>
    public const int WorkspaceInstructionsLength = 8000;

    public DbSet<AgentDefinitionEntity> AgentDefinitions => Set<AgentDefinitionEntity>();

    public DbSet<ImageDescriptionTokenUsageEntity> ImageDescriptionTokenUsage => Set<ImageDescriptionTokenUsageEntity>();

    public DbSet<ChatTokenUsageEntity> ChatTokenUsage => Set<ChatTokenUsageEntity>();

    public DbSet<ContentSafetyUsageEntity> ContentSafetyUsage => Set<ContentSafetyUsageEntity>();
    public DbSet<EmbeddingTokenUsageEntity> EmbeddingTokenUsage => Set<EmbeddingTokenUsageEntity>();
    public DbSet<WebhookSubscriptionEntity> WebhookSubscriptions => Set<WebhookSubscriptionEntity>();
    public DbSet<DeltaStateEntity> DeltaState => Set<DeltaStateEntity>();
    public DbSet<IndexedFileEntity> IndexedFiles => Set<IndexedFileEntity>();
    public DbSet<ChatWorkspaceEntity> ChatWorkspaces => Set<ChatWorkspaceEntity>();
    public DbSet<ChatConversationEntity> ChatConversations => Set<ChatConversationEntity>();
    public DbSet<ChatMessageEntity> ChatMessages => Set<ChatMessageEntity>();
    public DbSet<ChatMessageAttachmentFileEntity> ChatMessageAttachmentFiles => Set<ChatMessageAttachmentFileEntity>();
    public DbSet<ChatMessageAttachmentEntity> ChatMessageAttachments => Set<ChatMessageAttachmentEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<ImageDescriptionTokenUsageEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("NEWSEQUENTIALID()").ValueGeneratedOnAdd();
            entity.Property(x => x.ModelId).HasMaxLength(200);
            entity.HasIndex(x => new { x.UserId, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.UserId, x.Month });
            entity.HasIndex(x => new { x.ModelId, x.Month });
            entity.HasIndex(x => new { x.ModelId, x.CreatedAtUtc });
            entity.HasIndex(x => x.QuestionId);
            entity.HasIndex(x => x.ConversationId);
            entity.HasIndex(x => x.AttachmentId);
        });
        modelBuilder.Entity<ChatTokenUsageEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("NEWSEQUENTIALID()").ValueGeneratedOnAdd();
            entity.Property(x => x.ModelId).HasMaxLength(200);
            entity.Property(x => x.ToolNames).HasMaxLength(ToolListLength);
            entity.Property(x => x.SkillNames).HasMaxLength(ToolListLength);
            entity.Property(x => x.ScriptNames).HasMaxLength(ToolListLength);
            entity.HasIndex(x => new { x.UserId, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.UserId, x.Month });
            entity.HasIndex(x => new { x.ModelId, x.Month });
            entity.HasIndex(x => new { x.QuestionId, x.Sequence });
            entity.HasIndex(x => x.ConversationId);

            // Billed usage outlives its user: a user with recorded usage cannot be deleted out from
            // under the ledger, matching the restriction the per-turn table carried.
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ContentSafetyUsageEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Operation).HasMaxLength(50);
            entity.Property(x => x.ApiVersion).HasMaxLength(20);
            entity.Property(x => x.Status).HasMaxLength(20);
            entity.Property(x => x.ErrorCode).HasMaxLength(100);
            entity.Property(x => x.TraceId).HasMaxLength(32);
            entity.HasIndex(x => new { x.UserId, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.Status, x.CreatedAtUtc });
            entity.HasIndex(x => x.QuestionId);
            entity.HasIndex(x => x.AssessmentId);
            entity.HasIndex(x => x.AttachmentId);
        });
        modelBuilder.Entity<EmbeddingTokenUsageEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Operation).HasMaxLength(100);
            entity.Property(x => x.EmbeddingModelId).HasMaxLength(200);
            entity.Property(x => x.DeploymentId).HasMaxLength(200);
            entity.Property(x => x.DriveId).HasMaxLength(IdentifierLength);
            entity.Property(x => x.FileId).HasMaxLength(IdentifierLength);
            entity.Property(x => x.TraceId).HasMaxLength(32);
            entity.HasIndex(x => new { x.UserId, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.EmbeddingModelId, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.Operation, x.CreatedAtUtc });
            entity.HasIndex(x => x.QuestionId);
            entity.HasIndex(x => new { x.DriveId, x.FileId });
            entity.HasIndex(x => x.AttachmentId);
        });
        modelBuilder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(x => x.DisplayName).HasMaxLength(200);
            entity.Property(x => x.EntraTenantId).HasMaxLength(36);
            entity.Property(x => x.EntraObjectId).HasMaxLength(36);
            entity.HasIndex(x => x.NormalizedEmail).IsUnique().HasDatabaseName("EmailIndex").HasFilter("[NormalizedEmail] IS NOT NULL");
            entity.HasIndex(x => new { x.EntraTenantId, x.EntraObjectId }).IsUnique()
                .HasFilter("[EntraTenantId] IS NOT NULL AND [EntraObjectId] IS NOT NULL");
        });
        modelBuilder.Entity<IdentityRole<Guid>>().HasData(AppRoles.All.Select((name, index) => new IdentityRole<Guid>
        {
            Id = Guid.Parse($"00000000-0000-0000-0000-{index + 1:000000000000}"),
            Name = name,
            NormalizedName = name.ToUpperInvariant(),
            ConcurrencyStamp = $"app-role-{index + 1}"
        }));
        modelBuilder.Entity<ChatWorkspaceEntity>().HasIndex(x => x.CreatedById);
        modelBuilder.Entity<ChatWorkspaceEntity>().HasOne<ApplicationUser>().WithMany()
            .HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ChatConversationEntity>().HasIndex(x => x.CreatedById);
        modelBuilder.Entity<ChatMessageAttachmentFileEntity>().HasIndex(x => x.CreatedById);
        modelBuilder.Entity<ChatConversationEntity>().HasOne<ApplicationUser>().WithMany()
            .HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ChatMessageAttachmentFileEntity>().HasOne<ApplicationUser>().WithMany()
            .HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AgentDefinitionEntity>(entity =>
        {
            entity.ToTable("AgentDefinitions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("NEWSEQUENTIALID()").ValueGeneratedOnAdd();
            entity.Property(x => x.Name).HasMaxLength(100).IsRequired();
            entity.Property(x => x.ModelId).HasMaxLength(200);
            entity.Property(x => x.Instructions).IsRequired();
            entity.Property(x => x.CreatedAtUtc).HasPrecision(7);
            entity.Property(x => x.UpdatedAtUtc).HasPrecision(7);
            entity.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<WebhookSubscriptionEntity>(entity =>
        {
            entity.ToTable("WebhookSubscriptions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("NEWSEQUENTIALID()").ValueGeneratedOnAdd();
            entity.Property(x => x.GraphSubscriptionId).HasMaxLength(200);
            entity.Property(x => x.Name).HasMaxLength(100).IsRequired();
            entity.Property(x => x.NotificationUrl).HasMaxLength(2000).IsRequired();
            entity.Property(x => x.ClientState).HasMaxLength(128);
            entity.Property(x => x.CreatedAtUtc).HasPrecision(7);
            entity.Property(x => x.UpdatedAtUtc).HasPrecision(7);
            entity.HasIndex(x => x.Name).IsUnique();
            entity.HasIndex(x => x.GraphSubscriptionId)
                .IsUnique()
                .HasFilter("[GraphSubscriptionId] IS NOT NULL");
        });

        modelBuilder.Entity<DeltaStateEntity>(entity =>
        {
            entity.ToTable("SharePointDeltaState");
            entity.HasKey(x => x.DriveId);
            entity.Property(x => x.DriveId).HasMaxLength(IdentifierLength);
            entity.Property(x => x.DeltaLink).IsRequired();
            entity.Property(x => x.UpdatedAtUtc).HasPrecision(7);
        });

        modelBuilder.Entity<IndexedFileEntity>(entity =>
        {
            entity.ToTable("SharePointIndexedFiles");
            entity.HasKey(x => new { x.DriveId, x.ItemId });
            entity.Property(x => x.DriveId).HasMaxLength(IdentifierLength);
            entity.Property(x => x.ItemId).HasMaxLength(IdentifierLength);
            entity.Property(x => x.FileName).HasMaxLength(400).IsRequired();
            entity.Property(x => x.ParentPath).HasMaxLength(1000);
            entity.Property(x => x.WebUrl).HasMaxLength(2000);
            entity.Property(x => x.MimeType).HasMaxLength(200);
            entity.Property(x => x.ETag).HasMaxLength(200);
            entity.Property(x => x.CTag).HasMaxLength(200);
            // A base64 SHA-256 digest: always 44 ASCII characters, so CHAR(44) rather than NCHAR(44).
            entity.Property(x => x.PermissionsHash).HasMaxLength(44).IsFixedLength().IsUnicode(false).IsRequired();
            entity.Property(x => x.IndexFingerprint).HasMaxLength(200).IsRequired();
            entity.Property(x => x.SensitivityLabelId).HasMaxLength(36);
            entity.Property(x => x.SensitivityLabelName).HasMaxLength(255);
            entity.Property(x => x.SensitivityCheckedAtUtc).HasPrecision(7);
            entity.Property(x => x.LastModifiedUtc).HasPrecision(7);
            entity.Property(x => x.IndexedAtUtc).HasPrecision(7);

            // The orphan sweep and the operator listing both filter a drive by reconciliation round.
            entity.HasIndex(x => new { x.DriveId, x.ScanId });
        });

        modelBuilder.Entity<ChatWorkspaceEntity>(entity =>
        {
            entity.ToTable("ChatWorkspaces");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("NEWSEQUENTIALID()").ValueGeneratedOnAdd();
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();

            // Prompt text, so it is bounded by what a turn can afford to carry.
            entity.Property(x => x.Instructions).HasMaxLength(WorkspaceInstructionsLength);

            // The sandbox binding a grouped conversation uses instead of its own.
            entity.Property(x => x.FoundryEndpoint).HasMaxLength(2048);
            entity.Property(x => x.FoundrySessionId).HasMaxLength(200);
            entity.Property(x => x.CreatedAtUtc).HasPrecision(7);
            entity.Property(x => x.UpdatedAtUtc).HasPrecision(7);

            // The sidebar lists workspaces most recently used first, as it does conversations.
            entity.HasIndex(x => x.UpdatedAtUtc).IsDescending();
        });

        modelBuilder.Entity<ChatConversationEntity>(entity =>
        {
            entity.ToTable("ChatConversations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Title).HasMaxLength(200).IsRequired();
            entity.Property(x => x.UserId).HasMaxLength(200);
            entity.Property(x => x.FoundryEndpoint).HasMaxLength(2048);
            entity.Property(x => x.FoundrySessionId).HasMaxLength(200);
            entity.Property(x => x.CreatedAtUtc).HasPrecision(7);
            entity.Property(x => x.UpdatedAtUtc).HasPrecision(7);

            entity.HasOne(x => x.Agent)
                .WithMany()
                .HasForeignKey(x => x.AgentId)
                .OnDelete(DeleteBehavior.Restrict);

            // Deleting a workspace releases its conversations rather than taking them with it. They
            // keep their history and fall back to a sandbox of their own.
            entity.HasOne(x => x.Workspace)
                .WithMany(x => x.Conversations)
                .HasForeignKey(x => x.WorkspaceId)
                .OnDelete(DeleteBehavior.SetNull);

            // The sidebar groups the conversation list by workspace.
            entity.HasIndex(x => x.WorkspaceId);

            // The sidebar lists conversations most recently used first.
            entity.HasIndex(x => x.UpdatedAtUtc).IsDescending();
        });

        modelBuilder.Entity<ChatMessageEntity>(entity =>
        {
            entity.ToTable("ChatMessages");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("NEWSEQUENTIALID()").ValueGeneratedOnAdd();
            entity.Property(x => x.Content).IsRequired();
            entity.Property(x => x.ModelId).HasMaxLength(200);
            entity.Property(x => x.CreatedAtUtc).HasPrecision(7);

            // Both enums are stored by name, so a row is readable without the application.
            entity.Property(x => x.Role).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(x => x.Feedback).HasConversion<string>().HasMaxLength(10);

            entity.HasOne(x => x.Conversation)
                .WithMany(x => x.Messages)
                .HasForeignKey(x => x.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            // Messages are always read, and appended, in conversation order.
            entity.HasIndex(x => new { x.ConversationId, x.Sequence }).IsUnique();

            // The feedback review page reads only rated messages, newest first.
            entity.HasIndex(x => x.Feedback).HasFilter("[Feedback] IS NOT NULL");
        });

        modelBuilder.Entity<ChatMessageAttachmentFileEntity>(entity =>
        {
            entity.ToTable("ChatMessageAttachmentFiles");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("NEWSEQUENTIALID()").ValueGeneratedOnAdd();
            entity.Property(x => x.FileName).HasMaxLength(400).IsRequired();
            entity.Property(x => x.BlobName).HasMaxLength(800).IsRequired();
            entity.Property(x => x.ContentType).HasMaxLength(200);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(x => x.ErrorMessage).HasMaxLength(4000);
            entity.Property(x => x.CreatedAtUtc).HasPrecision(7);
            entity.Property(x => x.UpdatedAtUtc).HasPrecision(7);
            entity.Property(x => x.IndexedAtUtc).HasPrecision(7);
            entity.HasIndex(x => x.CreatedAtUtc).IsDescending();
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => x.ChatMessageAttachmentId).IsUnique()
                .HasFilter("[ChatMessageAttachmentId] IS NOT NULL");
            entity.HasOne(x => x.ChatMessageAttachment)
                .WithOne()
                .HasForeignKey<ChatMessageAttachmentFileEntity>(x => x.ChatMessageAttachmentId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ChatMessageAttachmentEntity>(entity =>
        {
            entity.ToTable("ChatMessageAttachments");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("NEWSEQUENTIALID()").ValueGeneratedOnAdd();
            entity.Property(x => x.CreatedAtUtc).HasPrecision(7);
            entity.HasIndex(x => new { x.MessageId, x.AttachmentFileId }).IsUnique();
            entity.HasOne(x => x.Message).WithMany(x => x.Attachments)
                .HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.AttachmentFile).WithMany(x => x.MessageAttachments)
                .HasForeignKey(x => x.AttachmentFileId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
