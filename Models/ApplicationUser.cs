using Microsoft.AspNetCore.Identity;

namespace Baratrum.Models 
{
    public class ApplicationUser : IdentityUser
    {
        public string? Language { get; set; }
        public string? tgChatId {get;set;}
        public bool FilterEnabled { get; set; } = true;
    }
}