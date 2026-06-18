using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Infrastructure;
using System.Security.Claims;

QuestPDF.Settings.License = LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<ReceiptService>();
builder.Services.AddDbContext<IeccDbContext>(opts =>
    opts.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, opts =>
    {
        opts.Cookie.Name     = "iecc_admin";
        opts.Cookie.HttpOnly = true;
        opts.Cookie.SameSite = SameSiteMode.Strict;
        opts.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        opts.ExpireTimeSpan  = TimeSpan.FromHours(8);
        opts.SlidingExpiration = true;
        opts.Events.OnRedirectToLogin        = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        opts.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

// ── DB schema on startup ───────────────────────────────────
await using (var scope = app.Services.CreateAsyncScope())
{
    var db   = scope.ServiceProvider.GetRequiredService<IeccDbContext>();
    var log  = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    for (var attempt = 1; attempt <= 10; attempt++)
    {
        try { await db.Database.OpenConnectionAsync(); db.Database.CloseConnection(); break; }
        catch { log.LogWarning("DB not ready (attempt {A}/10), retrying in 3s…", attempt); await Task.Delay(3000); }
    }
    try
    {
        await db.Database.MigrateAsync();
    }
    catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P07")
    {
        // Original tables exist from EnsureCreated with no migration history.
        // Create the history table, mark only InitialCreate as applied (those
        // tables already exist), then re-run MigrateAsync to create any new tables
        // added after the initial schema (e.g. SmtpSettings, PaypalSettings).
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                "MigrationId"    character varying(150) NOT NULL,
                "ProductVersion" character varying(32)  NOT NULL,
                CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
            )
            """);
        var initialMigration = db.Database.GetMigrations().First();
        await db.Database.ExecuteSqlRawAsync($"""
            INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            VALUES ('{initialMigration}', '8.0.11')
            ON CONFLICT DO NOTHING
            """);
        await db.Database.MigrateAsync();
    }
}

string? cachedToken    = null;
string? cachedClientId = null;
DateTime tokenExpiry   = DateTime.MinValue;
var tokenLock          = new SemaphoreSlim(1, 1);

async Task<(string clientId, string clientSecret, string ppBase)> GetPP(IeccDbContext db)
{
    var s = await db.PaypalSettings.FirstOrDefaultAsync();
    if (s is null || string.IsNullOrWhiteSpace(s.ClientId))
        throw new InvalidOperationException("PayPal settings have not been configured. Please set them in Admin → Settings.");
    return (s.ClientId, s.ClientSecret, s.Mode == "live" ? "https://api-m.paypal.com" : "https://api-m.sandbox.paypal.com");
}

async Task<string> GetToken(IHttpClientFactory f, string clientId, string clientSecret, string ppBase)
{
    if (cachedToken is not null && cachedClientId == clientId && DateTime.UtcNow < tokenExpiry) return cachedToken;
    await tokenLock.WaitAsync();
    try
    {
        if (cachedToken is not null && cachedClientId == clientId && DateTime.UtcNow < tokenExpiry) return cachedToken;
        using var client = f.CreateClient();
        var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
        client.DefaultRequestHeaders.Add("Authorization", $"Basic {creds}");
        var resp = await client.PostAsync($"{ppBase}/v1/oauth2/token",
            new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("grant_type", "client_credentials") }));
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        cachedToken    = doc.RootElement.GetProperty("access_token").GetString()!;
        cachedClientId = clientId;
        tokenExpiry    = DateTime.UtcNow.AddSeconds(doc.RootElement.GetProperty("expires_in").GetInt32() - 60);
        return cachedToken;
    }
    finally { tokenLock.Release(); }
}

// ════════════════════════════════════════════════════════════
// PUBLIC API
// ════════════════════════════════════════════════════════════

app.MapGet("/api/paypal/client-id", async (IeccDbContext db) =>
{
    var s = await db.PaypalSettings.FirstOrDefaultAsync();
    return Results.Ok(new { clientId = s?.ClientId ?? "" });
});

app.MapPost("/api/paypal/create-order", async (CreateOrderRequest req, IHttpClientFactory f, IeccDbContext db) =>
{
    var (clientId, clientSecret, ppBase) = await GetPP(db);
    var token = await GetToken(f, clientId, clientSecret, ppBase);
    using var client = f.CreateClient();
    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
    var payload = JsonSerializer.Serialize(new { intent = "CAPTURE", purchase_units = new[] { new { amount = new { currency_code = "USD", value = req.Amount.ToString("F2") }, description = "IECC Masjid Donation" } } });
    var resp = await client.PostAsync($"{ppBase}/v2/checkout/orders", new StringContent(payload, Encoding.UTF8, "application/json"));
    using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
    return Results.Ok(new { id = doc.RootElement.GetProperty("id").GetString() });
});

app.MapPost("/api/paypal/capture-order/{orderId}", async (string orderId, IHttpClientFactory f, IeccDbContext db, ReceiptService receipt) =>
{
    var (clientId, clientSecret, ppBase) = await GetPP(db);
    var token = await GetToken(f, clientId, clientSecret, ppBase);
    using var client = f.CreateClient();
    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
    client.DefaultRequestHeaders.Add("PayPal-Request-Id", Guid.NewGuid().ToString());
    var resp = await client.PostAsync($"{ppBase}/v2/checkout/orders/{orderId}/capture", new StringContent("{}", Encoding.UTF8, "application/json"));
    using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
    var status = doc.RootElement.GetProperty("status").GetString();
    string? payerEmail = null, payerName = null;
    decimal amount = 0;
    if (doc.RootElement.TryGetProperty("payer", out var payer))
    {
        payerEmail = payer.TryGetProperty("email_address", out var em) ? em.GetString() : null;
        if (payer.TryGetProperty("name", out var name))
            payerName = $"{(name.TryGetProperty("given_name", out var g) ? g.GetString() : "")} {(name.TryGetProperty("surname", out var s) ? s.GetString() : "")}".Trim();
    }
    if (doc.RootElement.TryGetProperty("purchase_units", out var units) && units.GetArrayLength() > 0 && units[0].TryGetProperty("payments", out var pay) && pay.TryGetProperty("captures", out var caps) && caps.GetArrayLength() > 0 && caps[0].TryGetProperty("amount", out var amt) && amt.TryGetProperty("value", out var val))
        decimal.TryParse(val.GetString(), out amount);
    if (status == "COMPLETED")
    {
        db.Donations.Add(new Donation { DonorName = payerName, DonorEmail = payerEmail, Amount = amount, Method = "PayPal", Frequency = "one-time", TransactionId = orderId, Status = "completed" });
        await db.SaveChangesAsync();
        if (!string.IsNullOrWhiteSpace(payerEmail))
        {
            var req = new ReceiptRequest(payerName ?? "Anonymous", payerEmail, amount, "PayPal", "one-time", orderId);
            _ = Task.Run(async () => { try { var pdf = receipt.GeneratePdf(req); await receipt.SendAsync(req, pdf); } catch (Exception ex) { app.Logger.LogError(ex, "PayPal receipt failed for {Email}", payerEmail); } });
        }
        return Results.Ok(new { status, payerEmail, payerName });
    }
    return Results.BadRequest(new { status });
});

app.MapPost("/api/receipt/send", async (ReceiptRequest req, ReceiptService receipt) =>
{
    if (string.IsNullOrWhiteSpace(req.DonorEmail)) return Results.BadRequest(new { error = "Email is required" });
    try { var pdf = receipt.GeneratePdf(req); await receipt.SendAsync(req, pdf); return Results.Ok(new { sent = true }); }
    catch (Exception ex) { app.Logger.LogError(ex, "Receipt failed for {Email}", req.DonorEmail); return Results.Problem(ex.Message); }
});

app.MapPost("/api/contact", async (ContactRequest req, IeccDbContext db, IConfiguration config, ILogger<Program> log) =>
{
    if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Message)) return Results.BadRequest(new { error = "Email and message are required" });
    db.ContactSubmissions.Add(new ContactSubmission { Name = req.Name, Email = req.Email, Phone = req.Phone, Subject = req.Subject, Message = req.Message });
    await db.SaveChangesAsync();
    _ = Task.Run(async () => { try { using var smtp = new MailKit.Net.Smtp.SmtpClient(); await smtp.ConnectAsync(config["Smtp:Host"]!, int.Parse(config["Smtp:Port"] ?? "587"), false); await smtp.AuthenticateAsync(config["Smtp:Username"]!, config["Smtp:Password"]!); var msg = new MimeKit.MimeMessage(); msg.From.Add(new MimeKit.MailboxAddress(config["Smtp:FromName"]!, config["Smtp:FromEmail"]!)); msg.To.Add(new MimeKit.MailboxAddress("IECC Admin", config["Smtp:Username"]!)); msg.ReplyTo.Add(new MimeKit.MailboxAddress(req.Name, req.Email)); msg.Subject = $"[Contact] {req.Subject ?? "General"} – {req.Name}"; msg.Body = new MimeKit.TextPart("plain") { Text = $"From: {req.Name}\nEmail: {req.Email}\nPhone: {req.Phone ?? "N/A"}\nSubject: {req.Subject}\n\nMessage:\n{req.Message}" }; await smtp.SendAsync(msg); await smtp.DisconnectAsync(true); } catch (Exception ex) { log.LogError(ex, "Contact email failed"); } });
    return Results.Ok(new { saved = true });
});

app.MapPost("/api/volunteer", async (VolunteerRequest req, IeccDbContext db, IConfiguration config, ILogger<Program> log) =>
{
    if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "Name and email are required" });
    db.VolunteerApplications.Add(new VolunteerApplication { Name = req.Name, Email = req.Email, Phone = req.Phone, Role = req.Role, Availability = req.Availability, Message = req.Message });
    await db.SaveChangesAsync();
    _ = Task.Run(async () => { try { using var smtp = new MailKit.Net.Smtp.SmtpClient(); await smtp.ConnectAsync(config["Smtp:Host"]!, int.Parse(config["Smtp:Port"] ?? "587"), false); await smtp.AuthenticateAsync(config["Smtp:Username"]!, config["Smtp:Password"]!); var msg = new MimeKit.MimeMessage(); msg.From.Add(new MimeKit.MailboxAddress(config["Smtp:FromName"]!, config["Smtp:FromEmail"]!)); msg.To.Add(new MimeKit.MailboxAddress("IECC Admin", config["Smtp:Username"]!)); msg.ReplyTo.Add(new MimeKit.MailboxAddress(req.Name, req.Email)); msg.Subject = $"[Volunteer] {req.Role} – {req.Name}"; msg.Body = new MimeKit.TextPart("plain") { Text = $"Name: {req.Name}\nEmail: {req.Email}\nPhone: {req.Phone ?? "N/A"}\nRole: {req.Role}\nAvailability: {req.Availability ?? "N/A"}\n\nMessage:\n{req.Message ?? "None"}" }; await smtp.SendAsync(msg); await smtp.DisconnectAsync(true); } catch (Exception ex) { log.LogError(ex, "Volunteer email failed"); } });
    return Results.Ok(new { saved = true });
});

app.MapPost("/api/newsletter", async (NewsletterRequest req, IeccDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(req.Email)) return Results.BadRequest(new { error = "Email required" });
    var existing = await db.NewsletterSubscribers.FirstOrDefaultAsync(s => s.Email == req.Email);
    if (existing is not null) { if (!existing.IsActive) { existing.IsActive = true; await db.SaveChangesAsync(); } return Results.Ok(new { subscribed = true }); }
    db.NewsletterSubscribers.Add(new NewsletterSubscriber { Email = req.Email });
    await db.SaveChangesAsync();
    return Results.Ok(new { subscribed = true });
});

app.MapGet("/api/events", async (IeccDbContext db) =>
    Results.Ok(await db.Events.Where(e => e.IsPublished && e.EventDate >= DateOnly.FromDateTime(DateTime.UtcNow)).OrderBy(e => e.EventDate).ThenBy(e => e.StartTime).Select(e => new { e.Id, e.Title, e.Description, e.Category, e.EventDate, e.StartTime, e.EndTime, e.Location }).ToListAsync()));

app.MapPost("/api/donation/zelle", async (ZelleDonationRequest req, IeccDbContext db) =>
{
    if (req.Amount <= 0) return Results.BadRequest(new { error = "Invalid amount" });
    db.Donations.Add(new Donation { DonorName = req.DonorName, DonorEmail = req.DonorEmail, Amount = req.Amount, Method = "Zelle", Frequency = req.Frequency ?? "one-time", TransactionId = $"ZELLE-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}", Status = "pending" });
    await db.SaveChangesAsync();
    return Results.Ok(new { saved = true });
});

// ════════════════════════════════════════════════════════════
// ADMIN AUTH
// ════════════════════════════════════════════════════════════

app.MapPost("/admin/login", async (LoginRequest req, HttpContext ctx, IConfiguration config) =>
{
    var adminUser = config["Admin:Username"] ?? "admin";
    var adminPass = config["Admin:Password"] ?? "";
    if (string.IsNullOrEmpty(adminPass)) return Results.Problem("Admin password not configured on this server.");
    if (req.Username != adminUser || req.Password != adminPass)
    {
        await Task.Delay(500);
        return Results.Json(new { error = "Invalid credentials" }, statusCode: 401);
    }
    var claims   = new[] { new Claim(ClaimTypes.Name, req.Username), new Claim(ClaimTypes.Role, "Admin") };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
        new AuthenticationProperties { IsPersistent = true });
    return Results.Ok(new { ok = true });
});

app.MapPost("/admin/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok();
});

app.MapGet("/admin/api/me", (HttpContext ctx) =>
    ctx.User.Identity?.IsAuthenticated == true
        ? Results.Ok(new { username = ctx.User.Identity.Name })
        : Results.Unauthorized()).RequireAuthorization();

// ════════════════════════════════════════════════════════════
// ADMIN DATA (all require authentication)
// ════════════════════════════════════════════════════════════

var adm = app.MapGroup("/admin/api").RequireAuthorization();

adm.MapGet("/stats", async (IeccDbContext db) => Results.Ok(new
{
    totalDonations    = await db.Donations.SumAsync(d => (decimal?)d.Amount) ?? 0m,
    donationCount     = await db.Donations.CountAsync(),
    pendingContacts   = await db.ContactSubmissions.CountAsync(c => !c.IsResolved),
    pendingVolunteers = await db.VolunteerApplications.CountAsync(v => v.Status == "pending"),
    subscribers       = await db.NewsletterSubscribers.CountAsync(s => s.IsActive),
    publishedEvents   = await db.Events.CountAsync(e => e.IsPublished)
}));

adm.MapGet("/donations", async (IeccDbContext db) =>
    Results.Ok(await db.Donations.OrderByDescending(d => d.CreatedAt)
        .Select(d => new { d.Id, d.DonorName, d.DonorEmail, d.Amount, d.Method, d.Frequency, d.Status, d.TransactionId, d.Notes, d.CreatedAt })
        .Take(500).ToListAsync()));

adm.MapPatch("/donations/{id:guid}/status", async (Guid id, UpdateStatusRequest req, IeccDbContext db) =>
{
    var d = await db.Donations.FindAsync(id);
    if (d is null) return Results.NotFound();
    d.Status = req.Status; await db.SaveChangesAsync();
    return Results.Ok(new { status = d.Status });
});

adm.MapGet("/contacts", async (IeccDbContext db) =>
    Results.Ok(await db.ContactSubmissions.OrderByDescending(c => c.CreatedAt)
        .Select(c => new { c.Id, c.Name, c.Email, c.Phone, c.Subject, c.Message, c.IsResolved, c.CreatedAt })
        .Take(500).ToListAsync()));

adm.MapPatch("/contacts/{id:guid}/resolve", async (Guid id, IeccDbContext db) =>
{
    var c = await db.ContactSubmissions.FindAsync(id);
    if (c is null) return Results.NotFound();
    c.IsResolved = !c.IsResolved; await db.SaveChangesAsync();
    return Results.Ok(new { isResolved = c.IsResolved });
});

adm.MapGet("/volunteers", async (IeccDbContext db) =>
    Results.Ok(await db.VolunteerApplications.OrderByDescending(v => v.CreatedAt)
        .Select(v => new { v.Id, v.Name, v.Email, v.Phone, v.Role, v.Availability, v.Message, v.Status, v.CreatedAt })
        .Take(500).ToListAsync()));

adm.MapPatch("/volunteers/{id:guid}/status", async (Guid id, UpdateStatusRequest req, IeccDbContext db) =>
{
    var v = await db.VolunteerApplications.FindAsync(id);
    if (v is null) return Results.NotFound();
    v.Status = req.Status; await db.SaveChangesAsync();
    return Results.Ok(new { status = v.Status });
});

adm.MapGet("/newsletter", async (IeccDbContext db) =>
    Results.Ok(await db.NewsletterSubscribers.OrderByDescending(s => s.SubscribedAt)
        .Select(s => new { s.Id, s.Email, s.IsActive, s.SubscribedAt })
        .Take(1000).ToListAsync()));

adm.MapPatch("/newsletter/{id:guid}/toggle", async (Guid id, IeccDbContext db) =>
{
    var s = await db.NewsletterSubscribers.FindAsync(id);
    if (s is null) return Results.NotFound();
    s.IsActive = !s.IsActive; await db.SaveChangesAsync();
    return Results.Ok(new { isActive = s.IsActive });
});

adm.MapGet("/events", async (IeccDbContext db) =>
    Results.Ok(await db.Events.OrderByDescending(e => e.EventDate)
        .Select(e => new { e.Id, e.Title, e.Description, e.Category, e.EventDate, e.StartTime, e.EndTime, e.Location, e.IsPublished })
        .Take(200).ToListAsync()));

adm.MapPost("/events", async (EventRequest req, IeccDbContext db) =>
{
    var ev = new CommunityEvent { Title = req.Title, Description = req.Description, Category = req.Category, EventDate = req.EventDate, StartTime = req.StartTime, EndTime = req.EndTime, Location = req.Location, IsPublished = req.IsPublished };
    db.Events.Add(ev); await db.SaveChangesAsync();
    return Results.Ok(new { id = ev.Id });
});

adm.MapPut("/events/{id:guid}", async (Guid id, EventRequest req, IeccDbContext db) =>
{
    var ev = await db.Events.FindAsync(id);
    if (ev is null) return Results.NotFound();
    ev.Title = req.Title; ev.Description = req.Description; ev.Category = req.Category;
    ev.EventDate = req.EventDate; ev.StartTime = req.StartTime; ev.EndTime = req.EndTime;
    ev.Location = req.Location; ev.IsPublished = req.IsPublished;
    await db.SaveChangesAsync(); return Results.Ok();
});

adm.MapDelete("/events/{id:guid}", async (Guid id, IeccDbContext db) =>
{
    var ev = await db.Events.FindAsync(id);
    if (ev is null) return Results.NotFound();
    db.Events.Remove(ev); await db.SaveChangesAsync(); return Results.Ok();
});

adm.MapGet("/settings/paypal", async (IeccDbContext db) =>
{
    var s = await db.PaypalSettings.FirstOrDefaultAsync();
    if (s is null) return Results.Ok(new { clientId = "", clientSecretSet = false, mode = "sandbox" });
    return Results.Ok(new { s.ClientId, clientSecretSet = !string.IsNullOrEmpty(s.ClientSecret), s.Mode });
});

adm.MapPost("/settings/paypal", async (PaypalSettingRequest req, IeccDbContext db) =>
{
    var s = await db.PaypalSettings.FirstOrDefaultAsync();
    if (s is null) { s = new PaypalSetting(); db.PaypalSettings.Add(s); }
    s.ClientId = req.ClientId;
    s.Mode     = req.Mode;
    if (!string.IsNullOrEmpty(req.ClientSecret)) s.ClientSecret = req.ClientSecret;
    await db.SaveChangesAsync();
    cachedToken = null; // invalidate cached token when credentials change
    return Results.Ok(new { saved = true });
});

adm.MapGet("/settings/smtp", async (IeccDbContext db) =>
{
    var s = await db.SmtpSettings.FirstOrDefaultAsync();
    if (s is null) return Results.Ok(new { host = "", port = 587, username = "", passwordSet = false, fromName = "IECC Masjid", fromEmail = "" });
    return Results.Ok(new { s.Host, s.Port, s.Username, passwordSet = !string.IsNullOrEmpty(s.Password), s.FromName, s.FromEmail });
});

adm.MapPost("/settings/smtp", async (SmtpSettingRequest req, IeccDbContext db) =>
{
    var s = await db.SmtpSettings.FirstOrDefaultAsync();
    if (s is null) { s = new SmtpSetting(); db.SmtpSettings.Add(s); }
    s.Host = req.Host; s.Port = req.Port; s.Username = req.Username;
    s.FromName = req.FromName; s.FromEmail = req.FromEmail;
    if (!string.IsNullOrEmpty(req.Password)) s.Password = req.Password;
    await db.SaveChangesAsync();
    return Results.Ok(new { saved = true });
});

adm.MapPost("/settings/smtp/test", async (IeccDbContext db, ReceiptService receipt) =>
{
    var s = await db.SmtpSettings.FirstOrDefaultAsync();
    if (s is null || string.IsNullOrWhiteSpace(s.Host)) return Results.BadRequest(new { error = "SMTP settings not configured yet" });
    var req = new ReceiptRequest("IECC Admin", s.FromEmail, 0m, "Test", "one-time", "TEST-" + DateTime.UtcNow.Ticks);
    try { var pdf = receipt.GeneratePdf(req); await receipt.SendAsync(req, pdf); return Results.Ok(new { sent = true }); }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

adm.MapPost("/donations/{id:guid}/resend-receipt", async (Guid id, IeccDbContext db, ReceiptService receipt) =>
{
    var d = await db.Donations.FindAsync(id);
    if (d is null) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(d.DonorEmail)) return Results.BadRequest(new { error = "No email on record for this donor" });
    var req = new ReceiptRequest(d.DonorName ?? "Anonymous", d.DonorEmail, d.Amount, d.Method, d.Frequency, d.TransactionId ?? d.Id.ToString());
    try { var pdf = receipt.GeneratePdf(req); await receipt.SendAsync(req, pdf); return Results.Ok(new { sent = true }); }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

app.Run();

record CreateOrderRequest(decimal Amount);
record ContactRequest(string Name, string Email, string? Phone, string? Subject, string Message);
record VolunteerRequest(string Name, string Email, string? Phone, string Role, string? Availability, string? Message);
record NewsletterRequest(string Email);
record ZelleDonationRequest(string? DonorName, string? DonorEmail, decimal Amount, string? Frequency);
record LoginRequest(string Username, string Password);
record UpdateStatusRequest(string Status);
record EventRequest(string Title, string? Description, string Category, DateOnly EventDate, TimeOnly? StartTime, TimeOnly? EndTime, string? Location, bool IsPublished);
record SmtpSettingRequest(string Host, int Port, string Username, string Password, string FromName, string FromEmail);
record PaypalSettingRequest(string ClientId, string ClientSecret, string Mode);
