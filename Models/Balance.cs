using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations.Schema;

namespace Baratrum.Models
{
    public class Balance
    {
        public int Id { get; set; }

        public required string UserId { get; set; }
        [ForeignKey(nameof(UserId))]
        public virtual ApplicationUser User { get; set; }

        public required decimal CurrentBalance { get; set; } 
        
        public required bool PaidMoneyBefore { get; set; }
    }
}