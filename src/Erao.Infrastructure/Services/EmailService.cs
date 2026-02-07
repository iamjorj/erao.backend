using Erao.Core.Interfaces;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Erao.Infrastructure.Services;

public class EmailService : IEmailService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
    {
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

    private async Task SendEmailAsync(string toEmail, string subject, string htmlBody)
    {
        try
        {
            var smtpHost = _configuration["Email:SmtpHost"];
            var smtpPort = int.Parse(_configuration["Email:SmtpPort"] ?? "587");
            var smtpUser = _configuration["Email:SmtpUser"];
            var smtpPassword = _configuration["Email:SmtpPassword"];
            var fromEmail = _configuration["Email:FromEmail"];
            var fromName = _configuration["Email:FromName"] ?? "Erao";

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(fromName, fromEmail));
            message.To.Add(new MailboxAddress(toEmail, toEmail));
            message.Subject = subject;

            var bodyBuilder = new BodyBuilder
            {
                HtmlBody = htmlBody
            };
            message.Body = bodyBuilder.ToMessageBody();

            using var client = new SmtpClient();
            await client.ConnectAsync(smtpHost, smtpPort, SecureSocketOptions.StartTls);
            await client.AuthenticateAsync(smtpUser, smtpPassword);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);

            _logger.LogInformation("Email sent successfully to {Email}", toEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {Email}", toEmail);
            throw;
        }
    }
}
