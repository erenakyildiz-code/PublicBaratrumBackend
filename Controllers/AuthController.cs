using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authorization;
using Baratrum.Services;
using Baratrum.Models;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly CustomUserManager _userManager;
    private readonly IEmailSender _emailSender;
    private readonly IWebHostEnvironment _env;

    // Inject your CustomUserManager along with the email tools
    public AuthController(CustomUserManager userManager, IEmailSender emailSender, IWebHostEnvironment env)
    {
        _userManager = userManager;
        _emailSender = emailSender;
        _env = env;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        // 1. Instantiate the default IdentityUser
        var user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email,
            Language = "en"
        };

        // 2. USE THE DEFAULT LOGIC: This hashes the password and saves it to the DB safely
        var result = await _userManager.CreateAsync(user, request.Password);

        // If the password is too weak, or email exists, return the default errors
        if (!result.Succeeded)
        {
            return BadRequest(result.Errors);
        }

        // 3. User is saved! Now generate the token for the email
        var token = await _userManager.GenerateEmailConfirmationTokenAsync(user);

        // Ensure this URL points to your frontend or API confirmation endpoint
        string confirmationLink = $"https://api.baratrum.com/api/Auth/confirmEmail?userId={user.Id}&code={Uri.EscapeDataString(token)}";

        // 4. Load the HTML template file asynchronously
        var templatePath = Path.Combine(_env.ContentRootPath, "Templates", "RegisterEmail.html");

        string htmlBody = await System.IO.File.ReadAllTextAsync(templatePath);

        // 5. Replace the placeholder token with the real link
        htmlBody = htmlBody.Replace("{{ConfirmationLink}}", confirmationLink);

        // 6. Fire off the email asynchronously
        _ = Task.Run(() => _emailSender.SendEmailAsync(request.Email, "Confirm e-mail", htmlBody));

        return Ok(new { message = "Registration successful. Verification email dispatched." });
    }

    [HttpGet("confirmEmail")]
    public async Task<IActionResult> ConfirmEmail([FromQuery] string userId, [FromQuery] string code)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(code))
        {
            return BadRequest("Invalid confirmation request.");
        }

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            return NotFound("User not found.");
        }
        var result = await _userManager.ConfirmEmailAsync(user, code);

        if (result.Succeeded)
        {
            // Return a clean success response or redirect to your frontend
            return Ok(new { message = "Email confirmed successfully." });
        }

        return BadRequest("Error confirming email: " + string.Join(", ", result.Errors.Select(e => e.Description)));
    }

    [Authorize]
    [HttpGet("checkLogin")]
    public async Task<IActionResult> CheckLogin()
    {
        return await Task.FromResult(Ok(new { message = "Authorized" }));
    }

    // The iOS app calls this from Profile -> Change Password.
    // Body: { "currentPassword": ..., "newPassword": ... }
    // 200 = success, 400 = validation error / wrong current password.
    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CurrentPassword) || string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return BadRequest("Current and new password are required.");
        }

        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return Unauthorized();
        }

        var result = await _userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            return BadRequest(result.Errors);
        }

        // Security notification email — fire-and-forget, same pattern as Register.
        if (!string.IsNullOrEmpty(user.Email))
        {
            _ = Task.Run(() => _emailSender.SendEmailAsync(user.Email, "Password changed",
                "<p>Your Baratrum password was just changed. If this wasn't you, reset your password immediately.</p>"));
        }

        return Ok(new { message = "Password updated." });
    }

    // The iOS app calls this from the login screen ("Forgot password?").
    // Enumeration-safe: always returns 200 with the same message, whether or
    // not the email exists. Only confirmed accounts get the email.
    //
    // NOTE: we deliberately do NOT use the stock Identity API /forgotPassword
    // endpoint — its built-in email contains a link that is unusable for an
    // API-only backend. Here we control the link: it points to a static
    // reset-password.html page served from this API's wwwroot.
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] BaratrumForgotPasswordRequest request)
    {
        const string safeMessage = "If that email is registered, a reset link is on its way.";

        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return Ok(new { message = safeMessage });
        }

        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user == null || !await _userManager.IsEmailConfirmedAsync(user))
        {
            return Ok(new { message = safeMessage });
        }

        // Same token mechanics as confirmEmail: raw token, URL-escaped into the link.
        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        string resetLink =
            $"https://api.baratrum.com/reset-password.html" +
            $"?email={Uri.EscapeDataString(request.Email)}" +
            $"&code={Uri.EscapeDataString(token)}";

        string htmlBody =
            "<p>You requested a password reset for your Baratrum account.</p>" +
            $"<p><a href='{resetLink}'>Click here to set a new password</a>.</p>" +
            "<p>If you didn't request this, you can ignore this email — your password stays unchanged.</p>";

        _ = Task.Run(() => _emailSender.SendEmailAsync(request.Email, "Reset password", htmlBody));

        return Ok(new { message = safeMessage });
    }

    // Called by the reset-password.html page (not directly by the app).
    // Body: { "email": ..., "code": ..., "newPassword": ... }
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] BaratrumResetPasswordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.Code) ||
            string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return BadRequest("Invalid reset request.");
        }

        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user == null)
        {
            // Don't reveal whether the account exists.
            return BadRequest("Invalid reset request.");
        }

        var result = await _userManager.ResetPasswordAsync(user, request.Code, request.NewPassword);
        if (!result.Succeeded)
        {
            return BadRequest(result.Errors);
        }

        // Security notification email.
        _ = Task.Run(() => _emailSender.SendEmailAsync(request.Email, "Password changed",
            "<p>Your Baratrum password was reset. If this wasn't you, contact support immediately.</p>"));

        return Ok(new { message = "Password reset." });
    }

    // The iOS app calls this from Profile -> Delete My Account.
    // Required by App Store Review Guideline 5.1.1(v): apps that support
    // account creation must offer in-app account deletion that works.
    [Authorize]
    [HttpDelete("deleteAccount")]
    public async Task<IActionResult> DeleteAccount()
    {
        // Get the currently logged-in user's ID from the JWT/cookie claims
        var userId = _userManager.GetUserId(User);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            return NotFound("User not found.");
        }

        var email = user.Email;

        // Invalidate any other sessions/cookies before removing the row.
        // (Identity API bearer tokens are self-contained and stay technically
        // valid until expiry, but every user lookup fails once the row is gone.)
        await _userManager.UpdateSecurityStampAsync(user);

        // DeleteAsync removes the user row; FKs with cascade take care of the rest
        var result = await _userManager.DeleteAsync(user);

        if (!result.Succeeded)
        {
            return BadRequest(result.Errors);
        }

        // Confirmation email — also useful as evidence of deletion.
        if (!string.IsNullOrEmpty(email))
        {
            _ = Task.Run(() => _emailSender.SendEmailAsync(email, "Account deleted",
                "<p>Your Baratrum account has been permanently deleted, along with its associated data.</p>"));
        }

        return Ok(new { message = "Account deleted." });
    }
}

public class ChangePasswordRequest
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

public class BaratrumForgotPasswordRequest
{
    public string Email { get; set; } = string.Empty;
}

public class BaratrumResetPasswordRequest
{
    public string Email { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}
