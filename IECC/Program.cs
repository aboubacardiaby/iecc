using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Infrastructure;

QuestPDF.Settings.License = LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<ReceiptService>();
builder.Services.AddDbContext<IeccDbContext>(opts =>
    opts.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

// ── Ensure DB schema exists on startup ─────────────────────
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IeccDbContext>();
    await db.Database.EnsureCreatedAsync();
}

var ppClientId     = app.Configuration["PayPal:ClientId"]     ?? "";
var ppClientSecret = app.Configuration["PayPal:ClientSecret"] ?? "";
var ppMode         = app.Configuration["PayPal:Mode"]         ?? "sandbox";
var ppBase         = ppMode == "live"
    ? "https://api-m.paypal.com"
    : "https://api-m.sandbox.paypal.com";

// ── PayPal token cache ─────────────────────────────────────
string? cachedToken  = null;
DateTime tokenExpiry = DateTime.MinValue;
var tokenLock        = new SemaphoreSlim(1, 1);

async Task<string> GetToken(IHttpClientFactory f)
{
    if (cachedToken is not null && DateTime.UtcNow < tokenExpiry) return cachedToken;
    await tokenLock.WaitAsync();
    try
    {
        if (cachedToken is not null && DateTime.UtcNow < tokenExpiry) return cachedToken;
        using var client = f.CreateClient();
        var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ppClientId}:{ppClientSecret}"));
        client.DefaultRequestHeaders.Add("Authorization", $"Basic {creds}");
        var resp = await client.PostAsync($"{ppBase}/v1/oauth2/token",
            new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("grant_type", "client_credentials") }));
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        cachedToken = doc.RootElement.GetProperty("access_token").GetString()!;
        tokenExpiry = DateTime.UtcNow.AddSeconds(doc.RootElement.GetProperty("expires_in").GetInt32() - 60);
        return cachedToken;
    }
    finally { tokenLock.Release(); }
}

// ── PayPal: client-id ─────────────────────────────────────
app.MapGet("/api/paypal/client-id", () => Results.Ok(new { clientId = ppClientId }));

// ── PayPal: create order ──────────────────────────────────
app.MapPost("/api/paypal/create-order", async (CreateOrderRequest req, IHttpClientFactory f) =>
{
    var token = await GetToken(f);
    using var client = f.CreateClient();
    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
    var payload = JsonSerializer.Serialize(new
    {
        intent = "CAPTURE",
        purchase_units = new[]
        {
            new { amount = new { currency_code = "USD", value = req.Amount.ToString("F2") }, description = "IECC Masjid Donation" }
        }
    });
    var resp = await client.PostAsync($"{ppBase}/v2/checkout/orders",
        new StringContent(payload, Encoding.UTF8, "application/json"));
    using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
    return Results.Ok(new { id = doc.RootElement.GetProperty("id").GetString() });
});

// ── PayPal: capture order → save donation ─────────────────
app.MapPost("/api/paypal/capture-order/{orderId}", async (string orderId, IHttpClientFactory f, IeccDbContext db) =>
{
    var token = await GetToken(f);
    using var client = f.CreateClient();
    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
    client.DefaultRequestHeaders.Add("PayPal-Request-Id", Guid.NewGuid().ToString());

    var resp = await client.PostAsync($"{ppBase}/v2/checkout/orders/{orderId}/capture",
        new StringContent("{}", Encoding.UTF8, "application/json"));
    using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());

    var status     = doc.RootElement.GetProperty("status").GetString();
    string? payerEmail = null, payerName = null;
    decimal amount = 0;

    if (doc.RootElement.TryGetProperty("payer", out var payer))
    {
        payerEmail = payer.TryGetProperty("email_address", out var em) ? em.GetString() : null;
        if (payer.TryGetProperty("name", out var name))
            payerName = $"{(name.TryGetProperty("given_name", out var g) ? g.GetString() : "")} {(name.TryGetProperty("surname", out var s) ? s.GetString() : "")}".Trim();
    }

    if (doc.RootElement.TryGetProperty("purchase_units", out var units) && units.GetArrayLength() > 0
        && units[0].TryGetProperty("payments", out var pay)
        && pay.TryGetProperty("captures", out var caps) && caps.GetArrayLength() > 0
        && caps[0].TryGetProperty("amount", out var amt)
        && amt.TryGetProperty("value", out var val))
        decimal.TryParse(val.GetString(), out amount);

    if (status == "COMPLETED")
    {
        db.Donations.Add(new Donation
        {
            DonorName     = payerName,
            DonorEmail    = payerEmail,
            Amount        = amount,
            Method        = "PayPal",
            Frequency     = "one-time",
            TransactionId = orderId,
            Status        = "completed"
        });
        await db.SaveChangesAsync();
        return Results.Ok(new { status, payerEmail, payerName });
    }

    return Results.BadRequest(new { status });
});

// ── Receipt ───────────────────────────────────────────────
app.MapPost("/api/receipt/send", async (ReceiptRequest req, ReceiptService receipt) =>
{
    if (string.IsNullOrWhiteSpace(req.DonorEmail))
        return Results.BadRequest(new { error = "Email is required" });
    try
    {
        var pdf = receipt.GeneratePdf(req);
        await receipt.SendAsync(req, pdf);
        return Results.Ok(new { sent = true });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Failed to send receipt to {Email}", req.DonorEmail);
        return Results.Problem($"Receipt could not be sent: {ex.Message}");
    }
});

// ── Contact form → DB + email notification ────────────────
app.MapPost("/api/contact", async (ContactRequest req, IeccDbContext db, IConfiguration config, ILogger<Program> log) =>
{
    if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Message))
        return Results.BadRequest(new { error = "Email and message are required" });

    db.ContactSubmissions.Add(new ContactSubmission
    {
        Name = req.Name, Email = req.Email, Phone = req.Phone, Subject = req.Subject, Message = req.Message
    });
    await db.SaveChangesAsync();

    _ = Task.Run(async () =>
    {
        try
        {
            using var smtp = new MailKit.Net.Smtp.SmtpClient();
            await smtp.ConnectAsync(config["Smtp:Host"]!, int.Parse(config["Smtp:Port"] ?? "587"), false);
            await smtp.AuthenticateAsync(config["Smtp:Username"]!, config["Smtp:Password"]!);
            var msg = new MimeKit.MimeMessage();
            msg.From.Add(new MimeKit.MailboxAddress(config["Smtp:FromName"]!, config["Smtp:FromEmail"]!));
            msg.To.Add(new MimeKit.MailboxAddress("IECC Admin", config["Smtp:Username"]!));
            msg.ReplyTo.Add(new MimeKit.MailboxAddress(req.Name, req.Email));
            msg.Subject = $"[Contact] {req.Subject ?? "General Inquiry"} – {req.Name}";
            msg.Body    = new MimeKit.TextPart("plain") { Text = $"From: {req.Name}\nEmail: {req.Email}\nPhone: {req.Phone ?? "N/A"}\nSubject: {req.Subject}\n\nMessage:\n{req.Message}" };
            await smtp.SendAsync(msg);
            await smtp.DisconnectAsync(true);
        }
        catch (Exception ex) { log.LogError(ex, "Contact email notification failed for {Email}", req.Email); }
    });

    return Results.Ok(new { saved = true });
});

// ── Volunteer form → DB + email notification ──────────────
app.MapPost("/api/volunteer", async (VolunteerRequest req, IeccDbContext db, IConfiguration config, ILogger<Program> log) =>
{
    if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Name))
        return Results.BadRequest(new { error = "Name and email are required" });

    db.VolunteerApplications.Add(new VolunteerApplication
    {
        Name = req.Name, Email = req.Email, Phone = req.Phone,
        Role = req.Role, Availability = req.Availability, Message = req.Message
    });
    await db.SaveChangesAsync();

    _ = Task.Run(async () =>
    {
        try
        {
            using var smtp = new MailKit.Net.Smtp.SmtpClient();
            await smtp.ConnectAsync(config["Smtp:Host"]!, int.Parse(config["Smtp:Port"] ?? "587"), false);
            await smtp.AuthenticateAsync(config["Smtp:Username"]!, config["Smtp:Password"]!);
            var msg = new MimeKit.MimeMessage();
            msg.From.Add(new MimeKit.MailboxAddress(config["Smtp:FromName"]!, config["Smtp:FromEmail"]!));
            msg.To.Add(new MimeKit.MailboxAddress("IECC Admin", config["Smtp:Username"]!));
            msg.ReplyTo.Add(new MimeKit.MailboxAddress(req.Name, req.Email));
            msg.Subject = $"[Volunteer] {req.Role} – {req.Name}";
            msg.Body    = new MimeKit.TextPart("plain") { Text = $"Name: {req.Name}\nEmail: {req.Email}\nPhone: {req.Phone ?? "N/A"}\nRole: {req.Role}\nAvailability: {req.Availability ?? "N/A"}\n\nMessage:\n{req.Message ?? "None"}" };
            await smtp.SendAsync(msg);
            await smtp.DisconnectAsync(true);
        }
        catch (Exception ex) { log.LogError(ex, "Volunteer email notification failed for {Email}", req.Email); }
    });

    return Results.Ok(new { saved = true });
});

// ── Newsletter subscribe ───────────────────────────────────
app.MapPost("/api/newsletter", async (NewsletterRequest req, IeccDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(req.Email))
        return Results.BadRequest(new { error = "Email is required" });

    var existing = await db.NewsletterSubscribers.FirstOrDefaultAsync(s => s.Email == req.Email);
    if (existing is not null)
    {
        if (!existing.IsActive) { existing.IsActive = true; await db.SaveChangesAsync(); }
        return Results.Ok(new { subscribed = true });
    }

    db.NewsletterSubscribers.Add(new NewsletterSubscriber { Email = req.Email });
    await db.SaveChangesAsync();
    return Results.Ok(new { subscribed = true });
});

// ── Events: public list ────────────────────────────────────
app.MapGet("/api/events", async (IeccDbContext db) =>
{
    var events = await db.Events
        .Where(e => e.IsPublished && e.EventDate >= DateOnly.FromDateTime(DateTime.UtcNow))
        .OrderBy(e => e.EventDate).ThenBy(e => e.StartTime)
        .Select(e => new { e.Id, e.Title, e.Description, e.Category, e.EventDate, e.StartTime, e.EndTime, e.Location })
        .ToListAsync();
    return Results.Ok(events);
});

// ── Zelle donation record (self-reported) ─────────────────
app.MapPost("/api/donation/zelle", async (ZelleDonationRequest req, IeccDbContext db) =>
{
    if (req.Amount <= 0)
        return Results.BadRequest(new { error = "Invalid amount" });

    db.Donations.Add(new Donation
    {
        DonorName     = req.DonorName,
        DonorEmail    = req.DonorEmail,
        Amount        = req.Amount,
        Method        = "Zelle",
        Frequency     = req.Frequency ?? "one-time",
        TransactionId = $"ZELLE-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
        Status        = "pending"
    });
    await db.SaveChangesAsync();
    return Results.Ok(new { saved = true });
});

app.Run();

record CreateOrderRequest(decimal Amount);
record ContactRequest(string Name, string Email, string? Phone, string? Subject, string Message);
record VolunteerRequest(string Name, string Email, string? Phone, string Role, string? Availability, string? Message);
record NewsletterRequest(string Email);
record ZelleDonationRequest(string? DonorName, string? DonorEmail, decimal Amount, string? Frequency);
