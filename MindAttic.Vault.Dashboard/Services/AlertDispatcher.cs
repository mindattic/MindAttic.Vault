using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MindAttic.Vault.Dashboard.Services;

/// <summary>
/// Fans an <see cref="AlertEvent"/> out to every configured channel (email +
/// webhooks). Each channel is best-effort and isolated — one failing channel
/// never blocks the others or the sweep. The dashboard itself is the always-on
/// third channel (it just reads the store), so this handles only the push paths.
/// </summary>
public sealed class AlertDispatcher
{
    private readonly MonitorOptions options;
    private readonly IHttpClientFactory httpFactory;
    private readonly ILogger<AlertDispatcher> log;

    public AlertDispatcher(IOptions<MonitorOptions> options, IHttpClientFactory httpFactory, ILogger<AlertDispatcher> log)
    {
        this.options = options.Value;
        this.httpFactory = httpFactory;
        this.log = log;
    }

    /// <summary>Dispatch one state-change alert to all push channels.</summary>
    public async Task DispatchAsync(AlertEvent e, CancellationToken ct)
    {
        var tasks = new List<Task>();
        if (options.Email is { } email && !string.IsNullOrWhiteSpace(email.Host) && email.To.Length > 0)
            tasks.Add(SafeSendEmailAsync(email, e, ct));
        foreach (var url in options.Webhooks.Where(u => !string.IsNullOrWhiteSpace(u)))
            tasks.Add(SafeSendWebhookAsync(url, e, ct));

        if (tasks.Count > 0)
            await Task.WhenAll(tasks);
    }

    private async Task SafeSendEmailAsync(EmailOptions email, AlertEvent e, CancellationToken ct)
    {
        try
        {
            var verb = e.Status == ProviderStatus.Healthy ? "RECOVERED" : "is DOWN";
            using var msg = new MailMessage
            {
                From = new MailAddress(email.From),
                Subject = $"[LLM Monitor] {e.DisplayName} {verb}",
                Body =
                    $"Provider : {e.DisplayName} ({e.ProviderId})\n" +
                    $"Status   : {e.Previous} -> {e.Status}\n" +
                    $"Diagnosis: {e.Diagnosis}\n" +
                    $"Detail   : {e.Detail}\n" +
                    $"At (UTC) : {e.AtUtc:u}\n",
            };
            foreach (var to in email.To)
                msg.To.Add(to);

            using var client = new SmtpClient(email.Host, email.Port) { EnableSsl = email.UseSsl };
            if (!string.IsNullOrWhiteSpace(email.Username))
                client.Credentials = new NetworkCredential(email.Username, email.Password);

            await client.SendMailAsync(msg, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "alert: email send failed for {Provider}", e.ProviderId);
        }
    }

    private async Task SafeSendWebhookAsync(string url, AlertEvent e, CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                provider = e.ProviderId,
                displayName = e.DisplayName,
                status = e.Status.ToString(),
                previous = e.Previous.ToString(),
                diagnosis = e.Diagnosis.ToString(),
                detail = e.Detail,
                atUtc = e.AtUtc,
            });

            var http = httpFactory.CreateClient();
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var res = await http.PostAsync(url, content, ct);
            if (!res.IsSuccessStatusCode)
                log.LogWarning("alert: webhook {Url} returned {Status}", url, (int)res.StatusCode);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "alert: webhook POST failed for {Url}", url);
        }
    }
}
