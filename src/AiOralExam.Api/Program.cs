using System.Security.Claims;
using System.Text.Json.Serialization;
using AiOralExam.Api.Infrastructure;
using AiOralExam.Modules.AccessConfig;
using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.Modules.Interview;
using AiOralExam.Modules.Reporting;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

var connectionString = config.GetConnectionString("Database")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Database in appsettings.");

// ---------------------------------------------------------------- Shared
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ICurrentUser, CurrentUser>();

// ---------------------------------------------------------------- Modules
// Each module registers its own DbContext + services. Modules talk to each other only through *.Contracts.
builder.Services
    .AddAccessConfigModule(connectionString, config)
    .AddInterviewModule(connectionString, config)
    .AddReportingModule();

// ---------------------------------------------------------------- Auth (Keycloak access tokens)
// Keycloak signs the tokens; the signing keys are read from {Authority}/.well-known/openid-configuration.
var keycloakAuthority = config["Keycloak:Authority"]
    ?? throw new InvalidOperationException("Missing Keycloak:Authority in appsettings.");
const string AccountInactive = "account_inactive";

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.Authority = keycloakAuthority;
        o.Audience = config["Keycloak:Audience"];
        o.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        o.MapInboundClaims = false; // keep the short claim names: sub, role, email
        o.TokenValidationParameters = new TokenValidationParameters
        {
            NameClaimType = AppClaims.Name,
            RoleClaimType = AppClaims.Role,
            ClockSkew = TimeSpan.FromMinutes(1),
        };
        // 401/403 also use the ApiError format
        o.Events = new JwtBearerEvents
        {
            // The database decides access: users.is_active must be true, and users.role_id replaces the
            // token's role, so a change made in AIVES applies at once even if Keycloak lags behind.
            // ponytail: one DB lookup per request; add a short MemoryCache if it shows up in profiling.
            OnTokenValidated = async ctx =>
            {
                var access = Guid.TryParse(ctx.Principal?.FindFirst(AppClaims.UserId)?.Value, out var userId)
                    ? await ctx.HttpContext.RequestServices.GetRequiredService<IExamConfigurationApi>()
                        .GetUserAccessAsync(userId, ctx.HttpContext.RequestAborted)
                    : null;
                if (access is not { IsActive: true })
                {
                    ctx.Fail(AccountInactive);
                    return;
                }

                var identity = (ClaimsIdentity)ctx.Principal!.Identity!;
                foreach (var claim in identity.FindAll(AppClaims.Role).ToList())
                    identity.RemoveClaim(claim);
                identity.AddClaim(new Claim(AppClaims.Role, access.Role));
            },
            OnChallenge = async ctx =>
            {
                ctx.HandleResponse();
                if (ctx.AuthenticateFailure?.Message == AccountInactive)
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await ctx.Response.WriteAsJsonAsync(new ApiError(AccountInactive,
                        "Your account is not active in AIVES. Contact an administrator.", ctx.HttpContext.TraceIdentifier));
                    return;
                }
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new ApiError("unauthenticated", "You need to sign in.", ctx.HttpContext.TraceIdentifier));
            },
            OnForbidden = async ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new ApiError("forbidden", "You do not have permission to perform this action.", ctx.HttpContext.TraceIdentifier));
            },
        };
    });
builder.Services.AddAuthorization();

// ---------------------------------------------------------------- MVC + validation errors
builder.Services
    .AddControllers()
    .AddApplicationPart(typeof(AccessConfigModule).Assembly)
    .AddApplicationPart(typeof(InterviewModule).Assembly)
    .AddApplicationPart(typeof(ReportingModule).Assembly)
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
    .ConfigureApiBehaviorOptions(o =>
    {
        // [Required]/[EmailAddress]... errors also use the ApiError format
        o.InvalidModelStateResponseFactory = ctx =>
        {
            var details = ctx.ModelState
                .Where(kv => kv.Value?.Errors.Count > 0)
                .ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value!.Errors.Select(e => string.IsNullOrEmpty(e.ErrorMessage) ? "Invalid value." : e.ErrorMessage).ToArray());
            return new BadRequestObjectResult(new ApiError(
                "validation_failed", "The submitted data is invalid.", ctx.HttpContext.TraceIdentifier, details));
        };
    });

// ---------------------------------------------------------------- CORS for the FE (ai-oral-exam-web)
var allowedOrigins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

// ---------------------------------------------------------------- Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.CustomSchemaIds(type => type.FullName!.Replace("+", "."));
    o.SwaggerDoc("v1", new OpenApiInfo { Title = "AIVES API", Version = "v1", Description = "AI-powered Viva Exam System" });
    // "Authorize" signs in through Keycloak (Authorization Code + PKCE, client aives-swagger)
    o.AddSecurityDefinition("Keycloak", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.OAuth2,
        Flows = new OpenApiOAuthFlows
        {
            AuthorizationCode = new OpenApiOAuthFlow
            {
                AuthorizationUrl = new Uri($"{keycloakAuthority}/protocol/openid-connect/auth"),
                TokenUrl = new Uri($"{keycloakAuthority}/protocol/openid-connect/token"),
                Scopes = new Dictionary<string, string> { ["openid"] = "OpenID Connect" },
            },
        },
    });
    o.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Keycloak" } }] = ["openid"]
    });
    foreach (var xml in Directory.GetFiles(AppContext.BaseDirectory, "AiOralExam*.xml"))
        o.IncludeXmlComments(xml);
});

// ---------------------------------------------------------------- Health check (database connectivity)
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database");

// ================================================================= Pipeline
var app = builder.Build();

app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(o =>
    {
        o.DocumentTitle = "AIVES API";
        o.OAuthClientId(config["Keycloak:SwaggerClientId"]);
        o.OAuthUsePkce();
    });
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

app.Run();

/// <summary>Checks that the API can reach PostgreSQL.</summary>
internal sealed class DatabaseHealthCheck(AccessConfigDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        await db.Database.CanConnectAsync(ct)
            ? HealthCheckResult.Healthy("PostgreSQL OK")
            : HealthCheckResult.Unhealthy("Cannot connect to PostgreSQL");
}

/// <summary>Lets WebApplicationFactory be used in integration tests.</summary>
public partial class Program;
