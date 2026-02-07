using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using FluentValidation;
using Erao.Application.Mappings;
using Erao.Application.Services;
using Erao.Application.Validators;
using Erao.Core.Interfaces;
using Erao.Infrastructure.Data;
using Erao.Infrastructure.Repositories;
using Erao.Infrastructure.Security;
using Erao.Infrastructure.Services;
using Erao.Infrastructure.Services.Parsers;

namespace Erao.API.Extensions;

public static class ServiceExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Database
        services.AddDbContext<EraoDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("DefaultConnection")));

        // Repositories
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // Security services
        services.AddScoped<IEncryptionService, EncryptionService>();
        services.AddScoped<ITokenService, TokenService>();

        // Application services
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IDatabaseConnectionService, DatabaseConnectionService>();
        services.AddScoped<IConversationService, ConversationService>();
        services.AddScoped<IChatService, ChatService>();
        services.AddScoped<IUsageService, UsageService>();
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<ISubscriptionService, SubscriptionService>();

        // External services
        services.AddHttpClient<IOllamaService, OllamaService>();
        services.AddScoped<IDatabaseQueryService, DatabaseQueryService>();
        services.AddScoped<IEmailService, EmailService>();
        services.AddHttpClient<IDodoPaymentsService, DodoPaymentsService>();

        // File parsing services
        services.AddScoped<IFileParser, ExcelFileParser>();
        services.AddScoped<IFileParser, WordFileParser>();
        services.AddScoped<IFileParser, CsvFileParser>();
        services.AddScoped<IFileDocumentService, FileDocumentService>();

        // MinIO storage
        services.AddSingleton<IMinioService, MinioService>();

        // AutoMapper
        services.AddAutoMapper(typeof(MappingProfile));

        // Validators
        services.AddValidatorsFromAssemblyContaining<RegisterRequestValidator>();

        return services;
    }

    public static IServiceCollection AddJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var secretKey = configuration["Jwt:SecretKey"]
            ?? throw new ArgumentNullException("Jwt:SecretKey not configured");
        var issuer = configuration["Jwt:Issuer"] ?? "Erao";
        var audience = configuration["Jwt:Audience"] ?? "Erao";

        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey)),
                ValidateIssuer = true,
                ValidIssuer = issuer,
                ValidateAudience = true,
                ValidAudience = audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            };
        });

        return services;
    }

    public static IServiceCollection AddSwaggerDocumentation(this IServiceCollection services)
    {
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Erao API",
                Version = "v1",
                Description = "AI-powered database intelligence platform API"
            });

            c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Description = "JWT Authorization header using the Bearer scheme. Enter 'Bearer' [space] and then your token",
                Name = "Authorization",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.ApiKey,
                Scheme = "Bearer"
            });

            c.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference
                        {
                            Type = ReferenceType.SecurityScheme,
                            Id = "Bearer"
                        }
                    },
                    Array.Empty<string>()
                }
            });
        });

        return services;
    }

    public static IServiceCollection AddCorsPolicies(this IServiceCollection services, IConfiguration configuration)
    {
        var allowedOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? new[] { "http://localhost:3000" };

        services.AddCors(options =>
        {
            options.AddPolicy("DefaultPolicy", builder =>
            {
                builder
                    .WithOrigins(allowedOrigins)
                    .AllowAnyMethod()
                    .AllowAnyHeader()
                    .AllowCredentials();
            });
        });

        return services;
    }
}
