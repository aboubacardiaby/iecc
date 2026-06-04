using Microsoft.EntityFrameworkCore;

// ── Entity Models ─────────────────────────────────────────

public class Donation
{
    public Guid     Id            { get; set; }
    public string?  DonorName     { get; set; }
    public string?  DonorEmail    { get; set; }
    public decimal  Amount        { get; set; }
    public string   Method        { get; set; } = "";       // PayPal | Zelle | Card
    public string   Frequency     { get; set; } = "one-time";
    public string?  TransactionId { get; set; }
    public string   Status        { get; set; } = "pending"; // pending | completed | failed
    public string?  Notes         { get; set; }
    public DateTime CreatedAt     { get; set; } = DateTime.UtcNow;
}

public class ContactSubmission
{
    public Guid     Id         { get; set; }
    public string   Name       { get; set; } = "";
    public string   Email      { get; set; } = "";
    public string?  Phone      { get; set; }
    public string?  Subject    { get; set; }
    public string   Message    { get; set; } = "";
    public bool     IsResolved { get; set; }
    public DateTime CreatedAt  { get; set; } = DateTime.UtcNow;
}

public class VolunteerApplication
{
    public Guid     Id           { get; set; }
    public string   Name         { get; set; } = "";
    public string   Email        { get; set; } = "";
    public string?  Phone        { get; set; }
    public string   Role         { get; set; } = "";
    public string?  Availability { get; set; }
    public string?  Message      { get; set; }
    public string   Status       { get; set; } = "pending"; // pending | contacted | active
    public DateTime CreatedAt    { get; set; } = DateTime.UtcNow;
}

public class NewsletterSubscriber
{
    public Guid     Id           { get; set; }
    public string   Email        { get; set; } = "";
    public bool     IsActive     { get; set; } = true;
    public DateTime SubscribedAt { get; set; } = DateTime.UtcNow;
}

public class CommunityEvent
{
    public Guid      Id          { get; set; }
    public string    Title       { get; set; } = "";
    public string?   Description { get; set; }
    public string    Category    { get; set; } = ""; // religious | educational | social | fundraising
    public DateOnly  EventDate   { get; set; }
    public TimeOnly? StartTime   { get; set; }
    public TimeOnly? EndTime     { get; set; }
    public string?   Location    { get; set; }
    public bool      IsPublished { get; set; } = true;
    public DateTime  CreatedAt   { get; set; } = DateTime.UtcNow;
}

// ── DbContext ─────────────────────────────────────────────

public class IeccDbContext(DbContextOptions<IeccDbContext> options) : DbContext(options)
{
    public DbSet<Donation>              Donations              { get; set; }
    public DbSet<ContactSubmission>     ContactSubmissions     { get; set; }
    public DbSet<VolunteerApplication>  VolunteerApplications  { get; set; }
    public DbSet<NewsletterSubscriber>  NewsletterSubscribers  { get; set; }
    public DbSet<CommunityEvent>        Events                 { get; set; }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Donation>(e =>
        {
            e.Property(d => d.Amount).HasColumnType("decimal(10,2)");
            e.Property(d => d.CreatedAt).HasDefaultValueSql("NOW()");
        });

        mb.Entity<ContactSubmission>(e =>
            e.Property(c => c.CreatedAt).HasDefaultValueSql("NOW()"));

        mb.Entity<VolunteerApplication>(e =>
            e.Property(v => v.CreatedAt).HasDefaultValueSql("NOW()"));

        mb.Entity<NewsletterSubscriber>(e =>
        {
            e.HasIndex(s => s.Email).IsUnique();
            e.Property(s => s.SubscribedAt).HasDefaultValueSql("NOW()");
        });

        mb.Entity<CommunityEvent>(e =>
            e.Property(ev => ev.CreatedAt).HasDefaultValueSql("NOW()"));
    }
}
