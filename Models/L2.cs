using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations.Schema;

namespace Baratrum.Models
{
    public class L2
    {
        public int Id { get; set; }
        
        public required string UserId { get; set; }
        [ForeignKey(nameof(UserId))]
        public virtual ApplicationUser User { get; set; } 

        public required long MemoryGenerationDate { get; set; } 
        public required string Memory {get;set;}
        public required bool MemoryConsumed {get;set;}
        public int? L3ID {get;set;} 
    }
}