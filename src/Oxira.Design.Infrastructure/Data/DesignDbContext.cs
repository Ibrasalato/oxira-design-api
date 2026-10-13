using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Logging;
using Oxira.Design.Application.Common;
using Oxira.Design.Domain;

namespace Oxira.Design.Infrastructure.Data;

public class DesignDbContext(DbContextOptions<DesignDbContext> options) : DbContext(options), IAppDbContext
{
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderFile> OrderFiles => Set<OrderFile>();
    public DbSet<OrderComment> OrderComments => Set<OrderComment>();
    public DbSet<Designer> Designers => Set<Designer>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<Purchase> Purchases => Set<Purchase>();
    public DbSet<RenderLog> RenderLogs => Set<RenderLog>();
    public DbSet<Listing> Listings => Set<Listing>();
    public DbSet<BuildLead> BuildLeads => Set<BuildLead>();
    public DbSet<CostRate> CostRates => Set<CostRate>();
    public DbSet<WaitlistEntry> Waitlist => Set<WaitlistEntry>();
    public DbSet<OptOut> OptOuts => Set<OptOut>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<UsageCounter> UsageCounters => Set<UsageCounter>();
    public DbSet<JobRun> JobRuns => Set<JobRun>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Lead>(e =>
        {
            e.Property(x => x.ContactName).HasMaxLength(120);
            e.Property(x => x.Phone).HasMaxLength(40);
            e.Property(x => x.Email).HasMaxLength(160);
            e.Property(x => x.City).HasMaxLength(120);
            e.Property(x => x.Package).HasMaxLength(40);
            e.Property(x => x.Area).HasMaxLength(40);
            e.Property(x => x.PreferredContact).HasMaxLength(200);
            e.Property(x => x.Channel).HasMaxLength(20);
            e.Property(x => x.Lang).HasMaxLength(5);
            e.Property(x => x.Page).HasMaxLength(300);
            e.Property(x => x.Context).HasMaxLength(4000);
            e.Property(x => x.Status).HasMaxLength(20);
            e.HasIndex(x => x.CreatedAt);
        });
        b.Entity<Order>(e =>
        {
            e.Property(x => x.Package).HasMaxLength(20);
            e.Property(x => x.ContactName).HasMaxLength(120);
            e.Property(x => x.Phone).HasMaxLength(40);
            e.Property(x => x.Email).HasMaxLength(160);
            e.Property(x => x.City).HasMaxLength(120);
            e.Property(x => x.Style).HasMaxLength(20);
            e.Property(x => x.Notes).HasMaxLength(3000);
            e.Property(x => x.FileNames).HasMaxLength(2000);
            e.Property(x => x.Rooms).HasMaxLength(4000);
            e.Property(x => x.PriceNote).HasMaxLength(200);
            e.Property(x => x.AmountDue).HasPrecision(10, 2);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Lang).HasMaxLength(5);
            e.Property(x => x.Page).HasMaxLength(300);
            e.Property(x => x.ReturnUrl).HasMaxLength(300);
            e.Property(x => x.InvoiceId).HasMaxLength(64);
            e.Property(x => x.Designer).HasMaxLength(160);
            e.Property(x => x.Review).HasMaxLength(1000);
            e.HasIndex(x => x.Email);
            e.HasIndex(x => x.Designer);
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.HasMany(x => x.Files).WithOne().HasForeignKey(f => f.OrderId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<OrderFile>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Mime).HasMaxLength(80);
            e.Property(x => x.Role).HasMaxLength(20);
            e.Property(x => x.Field).HasMaxLength(40);
            e.Property(x => x.Path).HasMaxLength(300);
        });
        b.Entity<OrderComment>(e =>
        {
            e.Property(x => x.Author).HasMaxLength(160);
            e.Property(x => x.Role).HasMaxLength(20);
            e.Property(x => x.Text).HasMaxLength(2000);
            e.HasIndex(x => new { x.OrderId, x.Id });
            e.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Designer>(e =>
        {
            e.Property(x => x.Email).HasMaxLength(160);
            e.Property(x => x.Name).HasMaxLength(80);
            e.Property(x => x.Skills).HasMaxLength(300);
            e.Property(x => x.Countries).HasMaxLength(120);
            e.Property(x => x.Note).HasMaxLength(1000);
            e.Property(x => x.Status).HasMaxLength(20);
            e.HasIndex(x => x.Email).IsUnique();
        });
        b.Entity<Project>(e =>
        {
            e.Property(x => x.Pid).HasMaxLength(14);
            e.Property(x => x.Owner).HasMaxLength(160);
            e.Property(x => x.Title).HasMaxLength(80);
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.ShareId).HasMaxLength(16);
            e.Property(x => x.Lang).HasMaxLength(5);
            e.HasIndex(x => x.Pid).IsUnique();
            e.HasIndex(x => x.ShareId).IsUnique();
            e.HasIndex(x => x.Owner);
            e.HasIndex(x => new { x.Public, x.UpdatedAt });
        });
        b.Entity<Wallet>(e =>
        {
            e.Property(x => x.Id).HasMaxLength(48);
            e.Property(x => x.Email).HasMaxLength(160);
        });
        b.Entity<Purchase>(e =>
        {
            e.Property(x => x.WalletId).HasMaxLength(48);
            e.Property(x => x.Pack).HasMaxLength(10);
            e.Property(x => x.Amount).HasPrecision(10, 2);
            e.Property(x => x.Email).HasMaxLength(160);
            e.Property(x => x.Lang).HasMaxLength(5);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.InvoiceId).HasMaxLength(64);
            e.HasIndex(x => x.WalletId);
        });
        b.Entity<RenderLog>(e =>
        {
            e.Property(x => x.Kind).HasMaxLength(10);
            e.Property(x => x.SessionId).HasMaxLength(64);
            e.Property(x => x.Ip).HasMaxLength(64);
            e.Property(x => x.Style).HasMaxLength(20);
            e.Property(x => x.View).HasMaxLength(20);
            e.Property(x => x.Mode).HasMaxLength(10);
            e.Property(x => x.Lang).HasMaxLength(5);
            e.HasIndex(x => x.CreatedAt);
        });
        b.Entity<Listing>(e =>
        {
            e.Property(x => x.Id).HasMaxLength(10);
            e.Property(x => x.Status).HasMaxLength(10);
            e.Property(x => x.Title).HasMaxLength(100);
            e.Property(x => x.City).HasMaxLength(60);
            e.Property(x => x.District).HasMaxLength(60);
            e.Property(x => x.Price).HasMaxLength(30);
            e.Property(x => x.Phone).HasMaxLength(30);
            e.Property(x => x.Email).HasMaxLength(160);
            e.Property(x => x.Lang).HasMaxLength(5);
            e.Property(x => x.Style).HasMaxLength(20);
            e.Property(x => x.Copy).HasMaxLength(8000);
            e.Property(x => x.InvoiceId).HasMaxLength(64);
        });
        b.Entity<BuildLead>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Phone).HasMaxLength(20);
            e.Property(x => x.Email).HasMaxLength(160);
            e.Property(x => x.Country).HasMaxLength(2);
            e.Property(x => x.City).HasMaxLength(40);
            e.Property(x => x.Level).HasMaxLength(20);
            e.Property(x => x.Soil).HasMaxLength(20);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Plan).HasMaxLength(80);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.Lang).HasMaxLength(5);
            e.Property(x => x.Page).HasMaxLength(600);
            e.Property(x => x.Status).HasMaxLength(20);
        });
        b.Entity<CostRate>(e =>
        {
            e.Property(x => x.Status).HasMaxLength(10);
            e.Property(x => x.Updated).HasMaxLength(7);
            e.Property(x => x.Token).HasMaxLength(32);
            e.Property(x => x.Notes).HasMaxLength(1500);
            e.Property(x => x.Sources).HasMaxLength(4000);
            e.HasIndex(x => new { x.Status, x.Id });
        });
        b.Entity<WaitlistEntry>(e =>
        {
            e.ToTable("Waitlist");
            e.Property(x => x.Email).HasMaxLength(160);
            e.Property(x => x.Plan).HasMaxLength(20);
            e.Property(x => x.Role).HasMaxLength(40);
            e.Property(x => x.Lang).HasMaxLength(5);
            e.Property(x => x.Country).HasMaxLength(2);
            e.HasIndex(x => x.Email).IsUnique();
        });
        b.Entity<OptOut>(e =>
        {
            e.Property(x => x.Email).HasMaxLength(160);
            e.Property(x => x.Reason).HasMaxLength(20);
            e.HasIndex(x => x.Email).IsUnique();
        });
        b.Entity<ChatMessage>(e =>
        {
            e.Property(x => x.SessionId).HasMaxLength(64);
            e.Property(x => x.Role).HasMaxLength(12);
            e.Property(x => x.Lang).HasMaxLength(5);
            e.Property(x => x.Page).HasMaxLength(300);
            e.HasIndex(x => new { x.SessionId, x.Id });
        });
        b.Entity<UsageCounter>(e =>
        {
            e.HasKey(x => new { x.Key, x.Day });
            e.Property(x => x.Key).HasMaxLength(120);
        });
        b.Entity<JobRun>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(60);
        });
    }
}

/// <summary>Lets `dotnet ef` build the model without starting the API.</summary>
public class DesignTimeFactory : IDesignTimeDbContextFactory<DesignDbContext>
{
    public DesignDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<DesignDbContext>()
        .UseSqlServer("Server=localhost;Database=OxiraDesign;Trusted_Connection=True;TrustServerCertificate=True").Options);
}

/// <summary>Applies migrations at startup (or creates the schema when the project has none yet).</summary>
public class DbInitializer(DesignDbContext db, ILogger<DbInitializer> log)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        if (db.Database.GetMigrations().Any()) await db.Database.MigrateAsync(ct);
        else await db.Database.EnsureCreatedAsync(ct);
        log.LogInformation("Database ready");
    }
}
