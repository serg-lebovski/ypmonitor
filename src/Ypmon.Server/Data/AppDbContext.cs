using Microsoft.EntityFrameworkCore;

namespace Ypmon.Server.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<MonitoredServer> Servers => Set<MonitoredServer>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<ServerSettings> Settings => Set<ServerSettings>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>().HasIndex(u => u.Username).IsUnique();

        b.Entity<MonitoredServer>().HasIndex(s => s.ApiKey).IsUnique();
        b.Entity<MonitoredServer>()
            .HasOne(s => s.Client)
            .WithMany(c => c.Servers)
            .HasForeignKey(s => s.ClientId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<Report>()
            .HasOne(r => r.Server)
            .WithMany(s => s.Reports)
            .HasForeignKey(r => r.ServerId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<Report>().HasIndex(r => new { r.ServerId, r.ReceivedAt });
    }
}
