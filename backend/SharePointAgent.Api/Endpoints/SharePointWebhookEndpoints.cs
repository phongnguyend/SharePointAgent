using System.Text;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;

namespace SharePointAgent.Api;

public static class SharePointWebhookEndpoints
{
    public static void MapSharePointWebhookEndpoints(this WebApplication app)
    {
        app.MapPost("/api/sharepoint/webhook", async (
            HttpRequest request,
            IChangeSignalPublisher publisher,
            SharePointClient sharePointClient,
            IWebhookSubscriptionRepository subscriptionRepository,
            IOptions<SharePointOptions> options,
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
        {
            if (request.Query.TryGetValue("validationToken", out var validationToken))
            {
                return Results.Text(validationToken.ToString(), "text/plain", Encoding.UTF8);
            }

            var envelope = await request.ReadFromJsonAsync<ChangeNotificationEnvelope>(cancellationToken);
            if (envelope is null)
            {
                return Results.BadRequest();
            }

            var driveId = await sharePointClient.GetDriveIdAsync(cancellationToken);
            var trackedSubscriptions = await subscriptionRepository.ListAsync(cancellationToken);

            foreach (var notification in envelope.Value)
            {
                var tracked = trackedSubscriptions.FirstOrDefault(item =>
                    string.Equals(item.GraphSubscriptionId, notification.SubscriptionId, StringComparison.Ordinal));
                var clientStateValid = tracked?.ClientState is { } customClientState
                    ? SubscriptionClientState.IsExactMatch(notification.ClientState, customClientState)
                    : SubscriptionClientState.IsValid(notification.ClientState, options.Value.ClientState);
                if (!clientStateValid)
                {
                    logger.LogWarning("Ignored a SharePoint notification with an invalid clientState.");
                    continue;
                }

                await publisher.PublishAsync(new SharePointChangeSignal(
                    driveId,
                    notification.SubscriptionId,
                    notification.ChangeType,
                    DateTimeOffset.UtcNow), cancellationToken);
            }
            return Results.Accepted();
        }).AllowAnonymous();
    }
}
