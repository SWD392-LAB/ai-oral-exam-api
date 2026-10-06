using System.Text;
using System.Text.Json.Serialization;
using AiOralExam.Api.Infrastructure;
using AiOralExam.Modules.AccessConfig;
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
    ?? throw new InvalidOperationException("Thiếu ConnectionStrings:Database trong appsettings.");

// ---------------------------------------------------------------- Shared
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ICurrentUser, CurrentUser>();

// ---------------------------------------------------------------- Modules
// Moi module tu dang ky DbContext + service cua minh. Module chi goi nhau qua *.Contracts.
builder.Services
    .AddAccessConfigModule(connectionString)
    .AddInterviewModule(connectionString, config)
    .AddReportingModule();

// ---------------------------------------------------------------- Auth (JWT)
builder.Services.Configure<JwtOptions>(config.GetSection(JwtOptions.SectionName));
var jwt = config.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwt.SigningKey) || jwt.SigningKey.Length < 32)
    throw new InvalidOperationException("Jwt:SigningKey phải dài tối thiểu 32 ký tự.");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false; // giu ten claim ngan: sub, role, email
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            NameClaimType = AppClaims.Name,
            RoleClaimType = AppClaims.Role,
            ClockSkew = TimeSpan.FromMinutes(1),
        };
        // 401/403 cung tra format ApiError
        o.Events = new JwtBearerEvents
        {
            OnChallenge = async ctx =>
            {
                ctx.HandleResponse();
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new ApiError("unauthenticated", "Bạn cần đăng nhập.", ctx.HttpContext.TraceIdentifier));
            },
            OnForbidden = async ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new ApiError("forbidden", "Bạn không có quyền thực hiện thao tác này.", ctx.HttpContext.TraceIdentifier));
            },
        };
    });
builder.Services.AddAuthorization();

// ---------------------------------------------------------------- MVC + loi validation
builder.Services
    .AddControllers()
    .AddApplicationPart(typeof(AccessConfigModule).Assembly)
    .AddApplicationPart(typeof(InterviewModule).Assembly)
    .AddApplicationPart(typeof(ReportingModule).Assembly)
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
    .ConfigureApiBehaviorOptions(o =>
    {
        // Loi [Required]/[EmailAddress]... cung tra format ApiError
        o.InvalidModelStateResponseFactory = ctx =>
        {
            var details = ctx.ModelState
                .Where(kv => kv.Value?.Errors.Count > 0)
                .ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value!.Errors.Select(e => string.IsNullOrEmpty(e.ErrorMessage) ? "Giá trị không hợp lệ." : e.ErrorMessage).ToArray());
            return new BadRequestObjectResult(new ApiError(
                "validation_failed", "Dữ liệu gửi lên không hợp lệ.", ctx.HttpContext.TraceIdentifier, details));
        };
    });

// ---------------------------------------------------------------- CORS cho FE (ai-oral-exam-web)
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
    o.SwaggerDoc("v1", new OpenApiInfo { Title = "AIVES API", Version = "v1", Description = "AI-powered Viva Exam System" });
    o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Dán accessToken nhận được từ POST /api/auth/login (không cần chữ 'Bearer ').",
    });
    o.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = []
    });
    foreach (var xml in Directory.GetFiles(AppContext.BaseDirectory, "AiOralExam*.xml"))
        o.IncludeXmlComments(xml);
});

// ---------------------------------------------------------------- Health check (kiem tra ket noi DB)
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database");

// ================================================================= Pipeline
var app = builder.Build();

app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(o => o.DocumentTitle = "AIVES API");
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

app.Run();

/// <summary>Kiem tra API ket noi duoc PostgreSQL.</summary>
internal sealed class DatabaseHealthCheck(AccessConfigDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        await db.Database.CanConnectAsync(ct)
            ? HealthCheckResult.Healthy("PostgreSQL OK")
            : HealthCheckResult.Unhealthy("Không kết nối được PostgreSQL");
}

/// <summary>Cho phep WebApplicationFactory dung trong integration test.</summary>
public partial class Program;
