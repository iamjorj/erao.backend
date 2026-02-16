using System.IO.Compression;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Erao.API.Extensions;
using Erao.API.Middleware;
using Erao.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/erao-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// Response compression (Brotli + gzip for 10-20x smaller JSON payloads)
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/json"]);
});
builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
    options.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(options =>
    options.Level = CompressionLevel.Fastest);

// Rate limiting
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // General API: 100 requests per minute per IP
    options.AddPolicy("general", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
            }));

    // Auth endpoints: 10 requests per minute per IP (login, register, forgot-password)
    options.AddPolicy("auth", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
            }));

    // AI chat: 20 requests per minute per IP
    options.AddPolicy("chat", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
            }));
});

// Custom service extensions
builder.Services.AddApplicationServices(builder.Configuration);
builder.Services.AddJwtAuthentication(builder.Configuration);
builder.Services.AddSwaggerDocumentation();
builder.Services.AddCorsPolicies(builder.Configuration);

var app = builder.Build();

// Auto-apply database migrations on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<EraoDbContext>();
    try
    {
        db.Database.Migrate();
        Log.Information("Database migrations applied successfully");
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Failed to apply database migrations. The app will start but database features may not work.");
    }
}

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Erao API V1");
        c.RoutePrefix = string.Empty;
    });
}

app.UseResponseCompression();

// Security headers
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["X-XSS-Protection"] = "1; mode=block";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    if (!app.Environment.IsDevelopment())
    {
        context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    }
    await next();
});

// Custom middleware
app.UseMiddleware<ExceptionMiddleware>();
app.UseMiddleware<ValidationMiddleware>();

app.UseCors("DefaultPolicy");

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Health check endpoint for Railway / Docker
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();
