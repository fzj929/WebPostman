using Microsoft.EntityFrameworkCore;

namespace WebPostman.Api.Data;

public sealed class WebPostmanDbContext(DbContextOptions<WebPostmanDbContext> options) : DbContext(options)
{
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<SessionEntity> Sessions => Set<SessionEntity>();
    public DbSet<ShareEntity> Shares => Set<ShareEntity>();
    public DbSet<WorkspaceStateEntity> Workspaces => Set<WorkspaceStateEntity>();
    public DbSet<CollectionEntity> Collections => Set<CollectionEntity>();
    public DbSet<EnvironmentEntity> Environments => Set<EnvironmentEntity>();
    public DbSet<HistoryEntity> History => Set<HistoryEntity>();
    public DbSet<DataImportEntity> DataImports => Set<DataImportEntity>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        if (Database.IsMySql()) model.UseCollation("utf8mb4_0900_bin").HasCharSet("utf8mb4");
        // Explicit sizes keep indexed keys within MySQL's utf8mb4 index limits.
        var user = model.Entity<UserEntity>();
        user.ToTable("wp_users"); user.HasKey(x => x.Id);
        user.Property(x => x.Id).HasMaxLength(32);
        user.Property(x => x.Email).HasMaxLength(254); user.HasIndex(x => x.Email).IsUnique();
        user.Property(x => x.PasswordHash).HasMaxLength(512);

        var session = model.Entity<SessionEntity>();
        session.ToTable("wp_sessions"); session.HasKey(x => x.TokenHash);
        session.Property(x => x.TokenHash).HasMaxLength(64); session.Property(x => x.UserId).HasMaxLength(32);
        session.HasIndex(x => x.Expires);
        session.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

        var share = model.Entity<ShareEntity>();
        share.ToTable("wp_shares"); share.HasKey(x => x.Id);
        share.Property(x => x.Id).HasMaxLength(32); share.Property(x => x.OwnerId).HasMaxLength(32);
        share.Property(x => x.Kind).HasMaxLength(16); share.Property(x => x.Title).HasMaxLength(100);
        share.Property(x => x.Description).HasMaxLength(1000);
        share.HasIndex(x => new { x.CreatedAt, x.Id }); share.HasIndex(x => x.OwnerId);
        share.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);

        var workspace = model.Entity<WorkspaceStateEntity>();
        workspace.ToTable("wp_workspaces"); workspace.HasKey(x => x.UserId);
        workspace.Property(x => x.UserId).HasMaxLength(32);
        workspace.HasOne<UserEntity>().WithOne().HasForeignKey<WorkspaceStateEntity>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

        var collection = model.Entity<CollectionEntity>();
        collection.ToTable("wp_collections"); collection.HasKey(x => new { x.UserId, x.Id });
        collection.Property(x => x.UserId).HasMaxLength(32); collection.Property(x => x.Id).HasMaxLength(200);
        collection.HasIndex(x => new { x.UserId, x.Position });
        collection.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

        var environment = model.Entity<EnvironmentEntity>();
        environment.ToTable("wp_environments"); environment.HasKey(x => new { x.UserId, x.Id });
        environment.Property(x => x.UserId).HasMaxLength(32); environment.Property(x => x.Id).HasMaxLength(200);
        environment.HasIndex(x => new { x.UserId, x.Position });
        environment.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

        var history = model.Entity<HistoryEntity>();
        history.ToTable("wp_history"); history.HasKey(x => new { x.UserId, x.Id });
        history.Property(x => x.UserId).HasMaxLength(32); history.Property(x => x.Id).HasMaxLength(200);
        history.HasIndex(x => new { x.UserId, x.At, x.Id });
        history.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

        var import = model.Entity<DataImportEntity>();
        import.ToTable("wp_data_imports"); import.HasKey(x => x.Id); import.Property(x => x.Id).HasMaxLength(100);
    }
}
