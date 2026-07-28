using Microsoft.AspNetCore.Identity;

namespace Baratrum.Models 
{
    public class ApplicationUser : IdentityUser
    {
        public string? Language { get; set; }
    }
}