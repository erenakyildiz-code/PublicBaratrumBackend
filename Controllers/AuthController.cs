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
}