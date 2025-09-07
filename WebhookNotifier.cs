using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RunCommandsService
{
    public class WebhookOptions
    {
        public bool Enabled { get; set; } = false;
        public string Url { get; set; } = null;
        public string AuthorizationHeader { get; set; } = null; // e.g., "Bearer xyz"
    }

    /// <summary>
    /// Minimal webhook notifier that POSTs subject+message as JSON to a configured URL.
    /// Implements IAlertNotifier so you can plug it into your alert pipeline if desired.
    /// </summary>
    public class WebhookNotifier : IAlertNotifier
    {
        private readonly WebhookOptions _opt;
        private readonly ILogger<WebhookNotifier> _logger;
        private static readonly HttpClient _http = new HttpClient();

        public WebhookNotifier(IOptions<WebhookOptions> options, ILogger<WebhookNotifier> logger)
        {
            _opt = options?.Value ?? new WebhookOptions();
            _logger = logger;
        }

        public async Task NotifyAsync(string subject, string message, CancellationToken ct = default)
        {
            if (!_opt.Enabled || string.IsNullOrWhiteSpace(_opt.Url)) return;

            try
            {
                var payload = new { subject, message, ts = DateTime.UtcNow.ToString("o") };
                var json = JsonSerializer.Serialize(payload);
                var req = new HttpRequestMessage(HttpMethod.Post, _opt.Url)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrWhiteSpace(_opt.AuthorizationHeader))
                    req.Headers.TryAddWithoutValidation("Authorization", _opt.AuthorizationHeader);

                var res = await _http.SendAsync(req, ct);
                res.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Webhook notify failed");
            }
        }
    }
}
