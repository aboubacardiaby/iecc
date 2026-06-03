using System.Text;
using System.Text.Json;
using QuestPDF.Infrastructure;

QuestPDF.Settings.License = LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<ReceiptService>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

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

// ── PayPal endpoints ───────────────────────────────────────

app.MapGet("/api/paypal/client-id", () => Results.Ok(new { clientId = ppClientId }));

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
            new
            {
                amount      = new { currency_code = "USD", value = req.Amount.ToString("F2") },
                description = "IECC Masjid Donation"
            }
        }
    });

    var resp = await client.PostAsync($"{ppBase}/v2/checkout/orders",
        new StringContent(payload, Encoding.UTF8, "application/json"));
    using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
    return Results.Ok(new { id = doc.RootElement.GetProperty("id").GetString() });
});

app.MapPost("/api/paypal/capture-order/{orderId}", async (string orderId, IHttpClientFactory f) =>
{
    var token = await GetToken(f);
    using var client = f.CreateClient();
    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
    client.DefaultRequestHeaders.Add("PayPal-Request-Id", Guid.NewGuid().ToString());

    var resp = await client.PostAsync($"{ppBase}/v2/checkout/orders/{orderId}/capture",
        new StringContent("{}", Encoding.UTF8, "application/json"));
    using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());

    var status = doc.RootElement.GetProperty("status").GetString();

    string? payerEmail = null;
    string? payerName  = null;
    if (doc.RootElement.TryGetProperty("payer", out var payer))
    {
        payerEmail = payer.TryGetProperty("email_address", out var em) ? em.GetString() : null;
        if (payer.TryGetProperty("name", out var name))
        {
            var given  = name.TryGetProperty("given_name", out var g) ? g.GetString() : "";
            var family = name.TryGetProperty("surname",    out var s) ? s.GetString() : "";
            payerName  = $"{given} {family}".Trim();
        }
    }

    return status == "COMPLETED"
        ? Results.Ok(new { status, payerEmail, payerName })
        : Results.BadRequest(new { status });
});

// ── Receipt endpoint ───────────────────────────────────────

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

// ── Contact form endpoint ──────────────────────────────
app.MapPost("/api/contact", async (ContactRequest req, IConfiguration config, ILogger<Program> log) =>
{
    if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Message))
        return Results.BadRequest(new { error = "Email and message are required" });
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
        msg.Body = new MimeKit.TextPart("plain")
        {
            Text = $"From: {req.Name}\nEmail: {req.Email}\nPhone: {req.Phone ?? "N/A"}\nSubject: {req.Subject}\n\nMessage:\n{req.Message}"
        };
        await smtp.SendAsync(msg);
        await smtp.DisconnectAsync(true);
        return Results.Ok(new { sent = true });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Failed to send contact email from {Email}", req.Email);
        return Results.Problem("Could not send message. Please email us directly.");
    }
});

// ── Volunteer form endpoint ────────────────────────────
app.MapPost("/api/volunteer", async (VolunteerRequest req, IConfiguration config, ILogger<Program> log) =>
{
    if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Name))
        return Results.BadRequest(new { error = "Name and email are required" });
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
        msg.Body = new MimeKit.TextPart("plain")
        {
            Text = $"Name: {req.Name}\nEmail: {req.Email}\nPhone: {req.Phone ?? "N/A"}\nRole: {req.Role}\nAvailability: {req.Availability ?? "N/A"}\n\nMessage:\n{req.Message ?? "None"}"
        };
        await smtp.SendAsync(msg);
        await smtp.DisconnectAsync(true);
        return Results.Ok(new { sent = true });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Failed to send volunteer email from {Email}", req.Email);
        return Results.Problem("Could not submit application. Please email us directly.");
    }
});

app.Run();

record CreateOrderRequest(decimal Amount);
record ContactRequest(string Name, string Email, string? Phone, string? Subject, string Message);
record VolunteerRequest(string Name, string Email, string? Phone, string Role, string? Availability, string? Message);
