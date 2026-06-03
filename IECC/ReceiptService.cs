using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

public record ReceiptRequest(
    string  DonorName,
    string  DonorEmail,
    decimal Amount,
    string  Method,
    string  Frequency,
    string  TransactionId
);

public class ReceiptService(IConfiguration config, ILogger<ReceiptService> logger)
{
    public byte[] GeneratePdf(ReceiptRequest req)
    {
        var receiptNum = $"IECC-{DateTime.UtcNow:yyyyMMdd}-{req.TransactionId[..Math.Min(8, req.TransactionId.Length)].ToUpper()}";
        var donor      = string.IsNullOrWhiteSpace(req.DonorName) ? "Anonymous" : req.DonorName;
        var freqLabel  = req.Frequency == "monthly" ? "Monthly Recurring" : "One-Time Donation";

        return Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(40);
            page.DefaultTextStyle(t => t.FontSize(11));

            page.Content().Column(col =>
            {
                // ── Header ──────────────────────────────────
                col.Item().Background("#1a3d2a").Padding(24).Row(row =>
                {
                    row.RelativeItem().Column(c =>
                    {
                        c.Item().Text("IECC MASJID")
                            .FontSize(26).Bold().FontColor("#e8a020");
                        c.Item().Text("ISLAMIC EDUCATION & CULTURAL CENTER")
                            .FontSize(8).FontColor("#90bfa8").LetterSpacing(1);
                        c.Item().PaddingTop(6)
                            .Text("4801 Welcome Ave North, Crystal, MN 55429")
                            .FontSize(9).FontColor("#7aaa90");
                    });
                    row.ConstantItem(100).AlignRight().AlignMiddle()
                        .Text("DONATION\nRECEIPT")
                        .FontSize(11).Bold().FontColor("#e8a020").LineHeight(1.4f);
                });

                // ── Receipt meta ─────────────────────────────
                col.Item().PaddingTop(20).PaddingBottom(4).Row(row =>
                {
                    row.RelativeItem().Text(t =>
                    {
                        t.Span("Receipt No: ").FontColor("#666");
                        t.Span(receiptNum).Bold();
                    });
                    row.ConstantItem(180).AlignRight().Text(t =>
                    {
                        t.Span("Date: ").FontColor("#666");
                        t.Span(DateTime.Now.ToString("MMMM d, yyyy")).Bold();
                    });
                });
                col.Item().LineHorizontal(1.5f).LineColor("#1a3d2a");

                // ── Donor info ───────────────────────────────
                col.Item().PaddingTop(18).Background("#f5f7f5").Padding(14).Column(c =>
                {
                    c.Item().Text("DONOR INFORMATION")
                        .FontSize(8).Bold().FontColor("#1a3d2a").LetterSpacing(1);
                    c.Item().PaddingTop(8).Row(r =>
                    {
                        r.ConstantItem(110).Text("Name:").FontColor("#555");
                        r.RelativeItem().Text(donor).Bold();
                    });
                    c.Item().PaddingTop(4).Row(r =>
                    {
                        r.ConstantItem(110).Text("Email:").FontColor("#555");
                        r.RelativeItem().Text(req.DonorEmail).Bold();
                    });
                });

                // ── Donation details ──────────────────────────
                col.Item().PaddingTop(14).Border(1).BorderColor("#e8c060").Padding(14).Column(c =>
                {
                    c.Item().Text("DONATION DETAILS")
                        .FontSize(8).Bold().FontColor("#1a3d2a").LetterSpacing(1);

                    void Row(string label, string value, bool big = false) =>
                        c.Item().PaddingTop(9).Row(r =>
                        {
                            r.ConstantItem(170).Text(label).FontColor("#555");
                            r.RelativeItem().Text(value).Bold()
                                .FontSize(big ? 15 : 11)
                                .FontColor(big ? "#1a3d2a" : "#111");
                        });

                    Row("Donation Amount:", $"${req.Amount:N2}", big: true);
                    Row("Payment Method:", req.Method);
                    Row("Donation Type:", freqLabel);
                    Row("Transaction ID:", req.TransactionId);
                    Row("Campaign:", "IECC Masjid Building Fund – Crystal, MN");
                });

                // ── Tax notice ───────────────────────────────
                col.Item().PaddingTop(20).Border(1).BorderColor("#1a3d2a").Padding(14).Column(c =>
                {
                    c.Item().Text("TAX DEDUCTIBILITY NOTICE")
                        .FontSize(8).Bold().FontColor("#1a3d2a").LetterSpacing(1);
                    c.Item().PaddingTop(8)
                        .Text("IECC is a registered 501(c)(3) non-profit organization. " +
                              "Your donation is tax-deductible to the extent allowed by law. " +
                              "No goods or services were provided in exchange for this contribution. " +
                              "Please retain this receipt for your tax records.")
                        .FontSize(9.5f).FontColor("#444").LineHeight(1.55f);
                });

                // ── Thank you ────────────────────────────────
                col.Item().PaddingTop(28).AlignCenter().Column(c =>
                {
                    c.Item().AlignCenter().Text("JazakAllahu Khayran")
                        .FontSize(18).Bold().FontColor("#1a3d2a").Italic();
                    c.Item().PaddingTop(8).AlignCenter()
                        .Text("May Allah accept your generous donation and reward you abundantly.")
                        .FontSize(10).FontColor("#555").LineHeight(1.5f);
                });

                // ── Footer ───────────────────────────────────
                col.Item().PaddingTop(30).LineHorizontal(1).LineColor("#ddd");
                col.Item().PaddingTop(6).AlignCenter()
                    .Text("IECC Masjid · 4801 Welcome Ave North, Crystal, MN 55429 · iecc.masjid@gmail.com")
                    .FontSize(8).FontColor("#aaa");
            });
        })).GeneratePdf();
    }

    public async Task SendAsync(ReceiptRequest req, byte[] pdf)
    {
        var host      = config["Smtp:Host"]      ?? throw new InvalidOperationException("SMTP host not configured");
        var port      = int.Parse(config["Smtp:Port"] ?? "587");
        var user      = config["Smtp:Username"]  ?? throw new InvalidOperationException("SMTP username not configured");
        var pass      = config["Smtp:Password"]  ?? throw new InvalidOperationException("SMTP password not configured");
        var fromName  = config["Smtp:FromName"]  ?? "IECC Masjid";
        var fromEmail = config["Smtp:FromEmail"] ?? user;

        var donor      = string.IsNullOrWhiteSpace(req.DonorName) ? "Valued Donor" : req.DonorName;
        var freqLabel  = req.Frequency == "monthly" ? "Monthly Recurring" : "One-Time";
        var receiptNum = $"IECC-{DateTime.UtcNow:yyyyMMdd}-{req.TransactionId[..Math.Min(8, req.TransactionId.Length)].ToUpper()}";

        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(fromName, fromEmail));
        msg.To.Add(new MailboxAddress(donor, req.DonorEmail));
        msg.Subject = $"Donation Receipt – IECC Masjid (${req.Amount:N2})";

        var html = $"""
            <div style="font-family:Arial,sans-serif;max-width:600px;margin:0 auto;border:1px solid #eee">
              <div style="background:#1a3d2a;padding:24px;text-align:center">
                <div style="color:#e8a020;font-size:22px;font-weight:bold;letter-spacing:1px">IECC MASJID</div>
                <div style="color:#90bfa8;font-size:10px;letter-spacing:2px;margin-top:4px">ISLAMIC EDUCATION &amp; CULTURAL CENTER</div>
              </div>
              <div style="padding:28px;background:#fff">
                <h2 style="color:#1a3d2a;margin:0 0 4px">Donation Receipt</h2>
                <p style="color:#999;font-size:12px;margin:0 0 20px">No: <strong>{receiptNum}</strong> &nbsp;&middot;&nbsp; {DateTime.Now:MMMM d, yyyy}</p>
                <p style="color:#444">Dear {donor},</p>
                <p style="color:#444;line-height:1.7">Thank you for your generous donation to <strong>IECC Masjid</strong>.
                   Your support helps us build a place of worship, education, and community for generations to come.</p>
                <table style="width:100%;border-collapse:collapse;margin:20px 0;font-size:14px">
                  <tr>
                    <td style="padding:11px 14px;background:#f5f7f5;color:#555;border:1px solid #e8e8e8">Amount</td>
                    <td style="padding:11px 14px;background:#f5f7f5;border:1px solid #e8e8e8;font-weight:bold;color:#1a3d2a;font-size:20px">${req.Amount:N2}</td>
                  </tr>
                  <tr>
                    <td style="padding:11px 14px;color:#555;border:1px solid #e8e8e8">Payment Method</td>
                    <td style="padding:11px 14px;border:1px solid #e8e8e8;font-weight:bold">{req.Method}</td>
                  </tr>
                  <tr>
                    <td style="padding:11px 14px;background:#f5f7f5;color:#555;border:1px solid #e8e8e8">Donation Type</td>
                    <td style="padding:11px 14px;background:#f5f7f5;border:1px solid #e8e8e8;font-weight:bold">{freqLabel}</td>
                  </tr>
                  <tr>
                    <td style="padding:11px 14px;color:#555;border:1px solid #e8e8e8">Transaction ID</td>
                    <td style="padding:11px 14px;border:1px solid #e8e8e8;font-weight:bold;font-family:monospace;font-size:12px">{req.TransactionId}</td>
                  </tr>
                  <tr>
                    <td style="padding:11px 14px;background:#f5f7f5;color:#555;border:1px solid #e8e8e8">Campaign</td>
                    <td style="padding:11px 14px;background:#f5f7f5;border:1px solid #e8e8e8;font-weight:bold">IECC Masjid Building Fund</td>
                  </tr>
                </table>
                <div style="background:#f0f8f0;border-left:4px solid #1a3d2a;padding:14px 16px;font-size:12px;color:#444;line-height:1.6;margin-bottom:24px">
                  <strong style="color:#1a3d2a">Tax Deductibility Notice:</strong> IECC is a registered 501(c)(3) non-profit organization.
                  Your donation is tax-deductible to the extent allowed by law.
                  No goods or services were provided in exchange for this contribution.
                  Please retain this receipt for your tax records.
                </div>
                <p style="text-align:center;font-size:20px;color:#1a3d2a;font-weight:bold;font-style:italic;margin:0">JazakAllahu Khayran</p>
                <p style="text-align:center;color:#666;font-style:italic;font-size:13px;margin-top:6px">
                  May Allah accept your generous donation and reward you abundantly.
                </p>
              </div>
              <div style="background:#1a3d2a;padding:14px;text-align:center;color:#7aaa90;font-size:11px">
                IECC Masjid &nbsp;&middot;&nbsp; 4801 Welcome Ave North, Crystal, MN 55429 &nbsp;&middot;&nbsp; iecc.masjid@gmail.com
              </div>
            </div>
            """;

        var body = new BodyBuilder { HtmlBody = html };
        body.Attachments.Add("IECC-Donation-Receipt.pdf", pdf, MimeKit.ContentType.Parse("application/pdf"));
        msg.Body = body.ToMessageBody();

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(host, port, SecureSocketOptions.StartTls);
        await smtp.AuthenticateAsync(user, pass);
        await smtp.SendAsync(msg);
        await smtp.DisconnectAsync(true);
        logger.LogInformation("Receipt sent to {Email} for ${Amount}", req.DonorEmail, req.Amount);
    }
}
