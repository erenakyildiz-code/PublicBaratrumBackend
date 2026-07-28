using Microsoft.AspNetCore.Identity.UI.Services;
using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging; 

namespace Baratrum.Services;

public class EmailSender : IEmailSender
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailSender> _logger;

    public EmailSender(IConfiguration configuration, ILogger<EmailSender> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendEmailAsync(string email, string subject, string htmlMessage)
    {
        try 
        {
            var mailServer = _configuration["EmailSettings:MailServer"];
            var mailPort = int.Parse(_configuration["EmailSettings:MailPort"] ?? "587");
            var senderName = _configuration["EmailSettings:SenderName"];
            var senderEmail = _configuration["EmailSettings:SenderEmail"];
            var password = _configuration["EmailSettings:Password"];

            using var client = new SmtpClient(mailServer, mailPort)
            {
                Credentials = new NetworkCredential(senderEmail, password),
                EnableSsl = true,
                UseDefaultCredentials = false
            };

            var mailMessage = new MailMessage
            {
                From = new MailAddress("no-reply@baratrum.com", senderName),
                Subject = subject,
                Body = htmlMessage,
                IsBodyHtml = true,
            };

            mailMessage.To.Add(email);

            _logger.LogInformation("Attempting to hand off email to {Server}:{Port} for {Recipient}", mailServer, mailPort, email);
            
            await client.SendMailAsync(mailMessage);
            
            _logger.LogInformation("Successfully handed off email for {Recipient}", email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FATAL: Failed to send email to {Recipient}. Reason: {Message}", email, ex.Message);
        }
    }
}