using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Crawler.Infrastructure.Persistence;

public sealed class CrawlerDbContext(DbContextOptions<CrawlerDbContext> options) : DbContext(options)
{
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<Page> Pages => Set<Page>();
    public DbSet<Edge> Edges => Set<Edge>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureJobs(modelBuilder);
        ConfigurePages(modelBuilder);
        ConfigureEdges(modelBuilder);
        UseSnakeCaseNames(modelBuilder);
    }

    private static void ConfigureJobs(ModelBuilder modelBuilder)
    {
        var job = modelBuilder.Entity<Job>();
        job.ToTable("jobs");
        job.HasKey(j => j.Id);
        job.Property(j => j.Status).HasConversion<string>().HasMaxLength(16);
        // History lists most recent first; id breaks ties so paging is stable.
        job.HasIndex(j => new { j.CreatedAt, j.Id }).IsDescending(true, false);
    }

    private static void ConfigurePages(ModelBuilder modelBuilder)
    {
        var page = modelBuilder.Entity<Page>();
        page.ToTable("pages");
        page.HasKey(p => p.Id);
        page.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
        page.HasOne<Job>().WithMany().HasForeignKey(p => p.JobId).OnDelete(DeleteBehavior.Cascade);
        page.HasOne<Page>().WithMany().HasForeignKey(p => p.ParentPageId).OnDelete(DeleteBehavior.Restrict);
        // Dedupe key: a URL is enqueued at most once per job, even under duplicate delivery.
        page.HasIndex(p => new { p.JobId, p.UrlHash }).IsUnique();
        page.HasIndex(p => new { p.JobId, p.ParentPageId });
    }

    private static void ConfigureEdges(ModelBuilder modelBuilder)
    {
        var edge = modelBuilder.Entity<Edge>();
        edge.ToTable("edges");
        edge.HasKey(e => new { e.FromPageId, e.ToUrlHash });
        edge.HasOne<Job>().WithMany().HasForeignKey(e => e.JobId).OnDelete(DeleteBehavior.Cascade);
        edge.HasOne<Page>().WithMany().HasForeignKey(e => e.FromPageId).OnDelete(DeleteBehavior.Cascade);
        edge.HasIndex(e => e.JobId);
    }

    /// <summary>
    /// Unquoted snake_case identifiers keep the worker's hand-written SQL (ON CONFLICT, FOR UPDATE) readable.
    /// </summary>
    private static void UseSnakeCaseNames(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }

            foreach (var key in entity.GetKeys())
            {
                key.SetName(ToSnakeCase(key.GetName()!));
            }

            foreach (var foreignKey in entity.GetForeignKeys())
            {
                foreignKey.SetConstraintName(ToSnakeCase(foreignKey.GetConstraintName()!));
            }

            foreach (var index in entity.GetIndexes())
            {
                index.SetDatabaseName(ToSnakeCase(index.GetDatabaseName()!));
            }
        }
    }

    private static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0 && name[i - 1] != '_' && !char.IsUpper(name[i - 1]))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}
