using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;

namespace SharePointAgent.Api;

public static class SubscriptionsEndpoints
{
    private const string NotificationUrlError =
        "'notificationUrl' must be an absolute HTTPS URL; Microsoft Graph calls it to validate the subscription before creating it.";

    public static void MapSubscriptionsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/subscriptions", (
            SubscriptionManager subscriptions,
            CancellationToken cancellationToken) =>
            CallGraphAsync(() => subscriptions.GetOverviewAsync(cancellationToken)));

        app.MapPost("/api/subscriptions", async (
            CreateSubscriptionRequest? body,
            SubscriptionManager subscriptions,
            CancellationToken cancellationToken) =>
        {
            var name = string.IsNullOrWhiteSpace(body?.Name) ? "Additional" : body.Name.Trim();
            if (string.Equals(name, WebhookSubscriptionDefaults.Name, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = "'Default' is reserved for the default subscription." });
            }
            if (!subscriptions.IsValidName(name))
            {
                return Results.BadRequest(new { error = "'name' is too long for Microsoft Graph clientState." });
            }

            var notificationUrl = string.IsNullOrWhiteSpace(body?.NotificationUrl) ? null : body!.NotificationUrl!.Trim();
            if (notificationUrl is not null && !SubscriptionManager.IsValidNotificationUrl(notificationUrl))
            {
                return Results.BadRequest(new { error = NotificationUrlError });
            }

            var clientState = string.IsNullOrWhiteSpace(body?.ClientState) ? null : body!.ClientState!.Trim();
            if (clientState is not null && !SubscriptionManager.IsValidCustomClientState(clientState))
            {
                return Results.BadRequest(new { error = "'clientState' must be 16-128 characters." });
            }

            return await CallGraphAsync(() => subscriptions.CreateAsync(name, body?.Days, notificationUrl, clientState, cancellationToken));
        });

        app.MapPost("/api/subscriptions/{id}/renew", (
            string id,
            SubscriptionLifetime? body,
            SubscriptionManager subscriptions,
            CancellationToken cancellationToken) =>
            CallGraphAsync(() => subscriptions.RenewAsync(id, body?.Days, cancellationToken)));

        app.MapPut("/api/subscriptions/{id:guid}/auto-renew", async (
            Guid id,
            SubscriptionAutoRenewRequest? body,
            SubscriptionManager subscriptions,
            CancellationToken cancellationToken) =>
        {
            if (body?.Enabled is null)
            {
                return Results.BadRequest(new { error = "'enabled' is required." });
            }

            return await CallGraphAsync(async () =>
            {
                await subscriptions.SetAutoRenewAsync(id, body.Enabled.Value, cancellationToken);
                return new { id, enabled = body.Enabled.Value };
            });
        });

        app.MapPut("/api/subscriptions/{id}", async (
            string id,
            CreateSubscriptionRequest? body,
            SubscriptionManager subscriptions,
            CancellationToken cancellationToken) =>
        {
            var name = body?.Name?.Trim();
            if (!string.IsNullOrWhiteSpace(name) && !subscriptions.IsValidName(name))
            {
                return Results.BadRequest(new { error = "'name' is too long for Microsoft Graph clientState." });
            }

            var notificationUrl = string.IsNullOrWhiteSpace(body?.NotificationUrl) ? null : body!.NotificationUrl!.Trim();
            if (notificationUrl is not null && !SubscriptionManager.IsValidNotificationUrl(notificationUrl))
            {
                return Results.BadRequest(new { error = NotificationUrlError });
            }

            var clientState = string.IsNullOrWhiteSpace(body?.ClientState) ? null : body!.ClientState!.Trim();
            if (clientState is not null && !SubscriptionManager.IsValidCustomClientState(clientState))
            {
                return Results.BadRequest(new { error = "'clientState' must be 16-128 characters." });
            }

            return await CallGraphAsync(() => subscriptions.UpdateAsync(id, name, body?.Days, notificationUrl, clientState, cancellationToken));
        });

        app.MapDelete("/api/subscriptions/{id}", (
            string id,
            SubscriptionManager subscriptions,
            CancellationToken cancellationToken) =>
            CallGraphAsync(async () =>
            {
                await subscriptions.DeleteAsync(id, cancellationToken);
                return new { deleted = id };
            }));
    }

    /// <summary>
    /// Runs a subscription operation and maps its failures to a status the caller can act on: a rule this
    /// API enforces becomes a 4xx, and a rejection from Microsoft Graph becomes a 502 carrying Graph's own
    /// message rather than an opaque 500.
    /// </summary>
    private static async Task<IResult> CallGraphAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return Results.Ok(await call());
        }
        catch (DuplicateNotificationUrlException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (DuplicateSubscriptionNameException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (ProtectedSubscriptionException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            return Results.Json(
                new { error = $"Microsoft Graph rejected the request: {ex.Message}" },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
