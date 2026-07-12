using Azure;
using Azure.Communication.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ArkansasServe.Functions.Services;

/// <summary>
/// Sends transactional email via Azure Communication Services (ACS). Optional by design:
/// when Communication__ConnectionString / Communication__SenderAddress are unset the service
/// is inert (<see cref="IsConfigured"/> is false and <see cref="SendAsync"/> is a no-op), so
/// the app runs identically with in-app notifications only until an ACS resource is provisioned.
/// </summary>
public class EmailService
{
    private readonly EmailClient? _client;
    private readonly string _senderAddress;
    private readonly ILogger<EmailService> _logger;

    /// <summary>True only when both an ACS connection string and a sender address are set.</summary>
    public bool IsConfigured => _client is not null && !string.IsNullOrWhiteSpace(_senderAddress);

    public EmailService(IConfiguration config, ILogger<EmailService> logger)
    {
        _logger = logger;

        // Read both the `__` and `:` forms (Azure's env-var provider exposes the
        // "Communication__*" app settings under "Communication:*"). Without the `:`
        // fallback this would stay disabled even after ACS is configured — same bug that
        // broke blob uploads.
        var connectionString = config["Communication__ConnectionString"] ?? config["Communication:ConnectionString"] ?? string.Empty;
        _senderAddress = config["Communication__SenderAddress"] ?? config["Communication:SenderAddress"] ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(connectionString))
            _client = new EmailClient(connectionString);

        if (!IsConfigured)
            _logger.LogInformation(
                "EmailService is not configured (Communication__ConnectionString / __SenderAddress unset); email notifications are disabled.");
    }

    /// <summary>
    /// Sends one email. No-op (returns) when the service is unconfigured or the recipient is
    /// empty. Throws on an actual ACS send failure so the caller can log/retry as appropriate.
    /// Uses <see cref="WaitUntil.Started"/> so it returns once the send is accepted rather than
    /// blocking the request thread on delivery.
    /// </summary>
    public async Task SendAsync(string toAddress, string subject, string plainText, string? html = null, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(toAddress))
            return;

        var content = new EmailContent(subject) { PlainText = plainText };
        if (!string.IsNullOrWhiteSpace(html))
            content.Html = html;

        var message = new EmailMessage(_senderAddress, toAddress, content);
        await _client!.SendAsync(WaitUntil.Started, message, cancellationToken);
    }
}
