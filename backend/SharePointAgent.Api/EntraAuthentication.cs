using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace SharePointAgent.Api;

public static class EntraAuthentication
{
    public const string Scope = "access_as_user";

    public static IServiceCollection AddEntraAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var tenantId = Guid.Parse(configuration["SharePoint:TenantId"] ?? "").ToString();
        var clientId = Guid.Parse(configuration["SharePoint:ClientId"] ?? "").ToString();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
            options.Audience = clientId;
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = $"https://login.microsoftonline.com/{tenantId}/v2.0",
                ValidateAudience = true,
                ValidAudience = clientId,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                NameClaimType = "name",
                RoleClaimType = "roles",
                ClockSkew = TimeSpan.FromMinutes(1)
            };
        });
        services.AddAuthorization(options =>
        {
            var policy = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireClaim("tid", tenantId)
                .RequireClaim("azp", clientId)
                .RequireClaim("oid")
                .RequireAssertion(context => context.User.FindAll("scp")
                    .Any(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(Scope)))
                .Build();
            options.DefaultPolicy = policy;
            options.FallbackPolicy = policy;
        });
        return services;
    }
}
