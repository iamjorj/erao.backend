using System.Net.Http.Json;
using System.Text.Json;
using Erao.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Erao.Infrastructure.Services;

public class EmailService : IEmailService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailService> _logger;

    public EmailService(HttpClient httpClient, IConfiguration configuration, ILogger<EmailService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendPasswordResetOtpAsync(string email, string otp)
    {
        var subject = "Erao - Password Reset Code";
        var body = $@"
            <html>
            <body style='font-family: Arial, sans-serif; line-height: 1.6; color: #333;'>
                <div style='max-width: 600px; margin: 0 auto; padding: 20px;'>
                    <h2 style='color: #2563eb;'>Password Reset Request</h2>
                    <p>You requested to reset your password for your Erao account.</p>
                    <p>Your verification code is:</p>
                    <div style='background-color: #f3f4f6; padding: 20px; text-align: center; border-radius: 8px; margin: 20px 0;'>
                        <span style='font-size: 32px; font-weight: bold; letter-spacing: 8px; color: #1f2937;'>{otp}</span>
                    </div>
                    <p>This code will expire in <strong>15 minutes</strong>.</p>
                    <p>If you didn't request this password reset, please ignore this email or contact support if you have concerns.</p>
                    <hr style='border: none; border-top: 1px solid #e5e7eb; margin: 30px 0;' />
                    <p style='color: #6b7280; font-size: 12px;'>This is an automated message from Erao. Please do not reply to this email.</p>
                </div>
            </body>
            </html>";

        await SendEmailAsync(email, subject, body);
    }

    public async Task SendWelcomeEmailAsync(string email, string firstName)
    {
        var subject = "Welcome to Erao!";
        var body = $@"
            <html>
            <body style='font-family: Arial, sans-serif; line-height: 1.6; color: #333;'>
                <div style='max-width: 600px; margin: 0 auto; padding: 20px;'>
                    <h2 style='color: #2563eb;'>Welcome to Erao, {firstName}!</h2>
                    <p>Thank you for joining Erao - your AI-powered database intelligence platform.</p>
                    <p>With Erao, you can:</p>
                    <ul>
                        <li>Query your databases using natural language</li>
                        <li>Get instant insights from your data</li>
                        <li>Connect multiple database types (PostgreSQL, MySQL, SQL Server, MongoDB)</li>
                    </ul>
                    <p>Get started by connecting your first database and asking a question!</p>
                    <hr style='border: none; border-top: 1px solid #e5e7eb; margin: 30px 0;' />
                    <p style='color: #6b7280; font-size: 12px;'>This is an automated message from Erao. Please do not reply to this email.</p>
                </div>
            </body>
            </html>";

        await SendEmailAsync(email, subject, body);
    }

    public async Task SendEmailVerificationOtpAsync(string email, string otp)
    {
        var subject = "Erao - Verify Your Email";
        var body = $@"
            <html>
            <body style='font-family: Arial, sans-serif; line-height: 1.6; color: #333;'>
                <div style='max-width: 600px; margin: 0 auto; padding: 20px;'>
                    <h2 style='color: #2563eb;'>Verify Your Email</h2>
                    <p>Thank you for signing up for Erao! Please verify your email address to complete your registration.</p>
                    <p>Your verification code is:</p>
                    <div style='background-color: #f3f4f6; padding: 20px; text-align: center; border-radius: 8px; margin: 20px 0;'>
                        <span style='font-size: 32px; font-weight: bold; letter-spacing: 8px; color: #1f2937;'>{otp}</span>
                    </div>
                    <p>This code will expire in <strong>15 minutes</strong>.</p>
                    <p>If you didn't create an account with Erao, please ignore this email.</p>
                    <hr style='border: none; border-top: 1px solid #e5e7eb; margin: 30px 0;' />
                    <p style='color: #6b7280; font-size: 12px;'>This is an automated message from Erao. Please do not reply to this email.</p>
                </div>
            </body>
            </html>";

        await SendEmailAsync(email, subject, body);
    }

    public async Task SendAdminOtpAsync(string email, string otp)
    {
        var subject = "Erao Admin - Verification Code";
        var body = $@"
            <html>
            <body style='font-family: Arial, sans-serif; line-height: 1.6; color: #333;'>
                <div style='max-width: 600px; margin: 0 auto; padding: 20px;'>
                    <h2 style='color: #000;'>Erao Admin Panel</h2>
                    <p>Your admin verification code is:</p>
                    <div style='background-color: #f3f4f6; padding: 20px; text-align: center; border-radius: 8px; margin: 20px 0;'>
                        <span style='font-size: 32px; font-weight: bold; letter-spacing: 8px; color: #1f2937;'>{otp}</span>
                    </div>
                    <p>This code will expire in <strong>10 minutes</strong>.</p>
                    <p>If you didn't request this code, please secure your account immediately.</p>
                    <hr style='border: none; border-top: 1px solid #e5e7eb; margin: 30px 0;' />
                    <p style='color: #6b7280; font-size: 12px;'>This is an automated message from Erao Admin. Please do not reply to this email.</p>
                </div>
            </body>
            </html>";

        await SendEmailAsync(email, subject, body);
    }

    private async Task SendEmailAsync(string toEmail, string subject, string htmlBody)
    {
        try
        {
            var apiKey = _configuration["Email:BrevoApiKey"];
            var fromEmail = _configuration["Email:FromEmail"];
            var fromName = _configuration["Email:FromName"] ?? "Erao";

            if (string.IsNullOrEmpty(apiKey))
            {
                _logger.LogError("Brevo API key not configured");
                throw new InvalidOperationException("Email service not configured");
            }

            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.brevo.com/v3/smtp/email");
            request.Headers.Add("api-key", apiKey);

            var payload = new
            {
                sender = new { name = fromName, email = fromEmail },
                to = new[] { new { email = toEmail } },
                subject = subject,
                htmlContent = htmlBody
            };

            request.Content = JsonContent.Create(payload);

            var response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Email sent successfully to {Email}", toEmail);
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to send email to {Email}. Status: {Status}, Error: {Error}",
                    toEmail, response.StatusCode, error);
                throw new Exception($"Failed to send email: {error}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {Email}", toEmail);
            throw;
        }
    }
}
