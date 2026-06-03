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

app.Run();

record CreateOrderRequest(decimal Amount);
