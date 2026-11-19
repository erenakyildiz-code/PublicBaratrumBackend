using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Baratrum.Models
{
    [Index(nameof(UserId), nameof(ContextName), IsUnique = true)]
    public class AgenticContextRecord
    {
        public int Id { get; set; }
        public required string ContextName {get;set;}
        
        public required string UserId { get; set; }
        [ForeignKey(nameof(UserId))]
        public virtual ApplicationUser User { get; set; } 

        public required long ContextGenerationDate { get; set; } 
        public required string Context {get;set;}
        public required string Goal {get;set;}
    }
}