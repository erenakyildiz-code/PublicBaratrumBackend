using Baratrum.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Baratrum.Models;

public class CustomUserManager : UserManager<ApplicationUser>
{
    private readonly AppDbContext _dbContext;

    public CustomUserManager(
        IUserStore<ApplicationUser> store,
        IOptions<IdentityOptions> optionsAccessor,
        IPasswordHasher<ApplicationUser> passwordHasher,
        IEnumerable<IUserValidator<ApplicationUser>> userValidators,
        IEnumerable<IPasswordValidator<ApplicationUser>> passwordValidators,
        ILookupNormalizer keyNormalizer,
        IdentityErrorDescriber errors,
        IServiceProvider services,
        ILogger<UserManager<ApplicationUser>> logger,
        AppDbContext dbContext) 
        : base(store, optionsAccessor, passwordHasher, userValidators, passwordValidators, keyNormalizer, errors, services, logger)
    {
        _dbContext = dbContext;
    }

    public override async Task<IdentityResult> ConfirmEmailAsync(ApplicationUser user, string token)
    {
        // 1. Let the default Identity logic run first
        var result = await base.ConfirmEmailAsync(user, token);

        if (result.Succeeded)
        {
            // Optional: Check if a balance already exists to prevent duplicates if they click the link twice
            var balanceExists = await _dbContext.Balances.AnyAsync(b => b.UserId == user.Id);
            
            if (!balanceExists)
            {
                // Create the new balance entry
                var newBalance = new Balance
                {
                    UserId = user.Id,
                    CurrentBalance = 1.00m,
                    PaidMoneyBefore = false
                };

                _dbContext.Balances.Add(newBalance);
                await _dbContext.SaveChangesAsync();
            }
        }

        return result;
    }
}