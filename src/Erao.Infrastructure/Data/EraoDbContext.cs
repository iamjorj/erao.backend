using Microsoft.EntityFrameworkCore;
using Erao.Core.Entities;

namespace Erao.Infrastructure.Data;

public class EraoDbContext : DbContext
{
    public EraoDbContext(DbContextOptions<EraoDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users { get; set; }
    public DbSet<DatabaseConnection> DatabaseConnections { get; set; }
    public DbSet<Conversation> Conversations { get; set; }
    public DbSet<Message> Messages { get; set; }
    public DbSet<UsageLog> UsageLogs { get; set; }
    public DbSet<FileDocument> FileDocuments { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // User configuration
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Email).IsUnique();
            entity.Property(e => e.Email).IsRequired().HasMaxLength(255);
            entity.Property(e => e.PasswordHash).IsRequired();
            entity.Property(e => e.FirstName).HasMaxLength(100);
            entity.Property(e => e.LastName).HasMaxLength(100);
            entity.Property(e => e.SubscriptionTier).HasConversion<int>();
        });

        // DatabaseConnection configuration
        modelBuilder.Entity<DatabaseConnection>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.DatabaseType).HasConversion<int>();
            entity.Property(e => e.EncryptedHost).IsRequired();
            entity.Property(e => e.EncryptedPort).IsRequired();
            entity.Property(e => e.EncryptedDatabaseName).IsRequired();
            entity.Property(e => e.EncryptedUsername).IsRequired();
            entity.Property(e => e.EncryptedPassword).IsRequired();
            entity.Property(e => e.SchemaCache).HasColumnType("text");

            entity.HasOne(e => e.User)
                .WithMany(u => u.DatabaseConnections)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // FileDocument configuration
        modelBuilder.Entity<FileDocument>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FileName).IsRequired().HasMaxLength(500);
            entity.Property(e => e.OriginalFileName).IsRequired().HasMaxLength(500);
            entity.Property(e => e.FileType).HasConversion<int>();
            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.ParsedContent).HasColumnType("text");
            entity.Property(e => e.SchemaInfo).HasColumnType("text");
            entity.Property(e => e.StoragePath).HasMaxLength(1000);
            entity.Property(e => e.ErrorMessage).HasMaxLength(2000);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Conversation configuration
        modelBuilder.Entity<Conversation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).HasMaxLength(500);

            entity.HasOne(e => e.User)
                .WithMany(u => u.Conversations)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.DatabaseConnection)
                .WithMany(d => d.Conversations)
                .HasForeignKey(e => e.DatabaseConnectionId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.FileDocument)
                .WithMany(f => f.Conversations)
                .HasForeignKey(e => e.FileDocumentId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Message configuration
        modelBuilder.Entity<Message>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Role).HasConversion<int>();
            entity.Property(e => e.Content).IsRequired();
            entity.Property(e => e.QueryResult).HasColumnType("text");

            entity.HasOne(e => e.Conversation)
                .WithMany(c => c.Messages)
                .HasForeignKey(e => e.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // UsageLog configuration
        modelBuilder.Entity<UsageLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.QueryType).HasMaxLength(100);

            entity.HasOne(e => e.User)
                .WithMany(u => u.UsageLogs)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.DatabaseConnection)
                .WithMany(d => d.UsageLogs)
                .HasForeignKey(e => e.DatabaseConnectionId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var entries = ChangeTracker.Entries<BaseEntity>();

        foreach (var entry in entries)
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = DateTime.UtcNow;
                entry.Entity.UpdatedAt = DateTime.UtcNow;
                if (entry.Entity.Id == Guid.Empty)
                {
                    entry.Entity.Id = Guid.NewGuid();
                }
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = DateTime.UtcNow;
            }
        }

        return base.SaveChangesAsync(cancellationToken);
    }
}
