using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations.Schema;

namespace Baratrum.Models
{
    public class Payment
    {
        public int Id { get; set; }
        
        // Foreign Key for Identity User
        public required string UserId { get; set; }
        [ForeignKey(nameof(UserId))]
        public virtual ApplicationUser User { get; set; } 

        // Stored as a long to hold the Unix timestamp
        public required long PaymentDate { get; set; } 
        
        public required string PaymentProof { get; set; }
        
        // Using decimal for financial accuracy
        public required decimal PaymentAmount { get; set; } 
    }
}