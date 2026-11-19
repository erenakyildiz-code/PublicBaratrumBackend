using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Baratrum.Models;
using Baratrum.Services;
using Baratrum.Data;

// RevenueCat -> this endpoint, server-to-server, whenever a purchase happens.
// No [Authorize]: RevenueCat is the caller. Instead we verify a shared secret
// sent in the Authorization header (you set this value yourself in the
// RevenueCat dashboard -> Integrations -> Webhooks).
[ApiController]
public class RevenueCatWebhookController : ControllerBase
{
    private readonly CustomUserManager _userManager;
    private readonly AppDbContext _db;  
    private readonly IConfiguration _config;
    private readonly ILogger<RevenueCatWebhookController> _logger;

    // Map RevenueCat product identifiers -> balance credit in USD.
    // Keys MUST match the product IDs from App Store Connect / RevenueCat
    // (e.g. "credits_10"). Values are what the user's balance is credited,
    // not necessarily the price paid.
    private static readonly Dictionary<string, decimal> CreditPacks = new()
    {
        ["credits_5"]  = 5.00m,
        ["credits_10"] = 10.00m,
        ["credits_25"] = 25.00m,
    };

    public RevenueCatWebhookController(
        CustomUserManager userManager,
        AppDbContext db,
        IConfiguration config,
        ILogger<RevenueCatWebhookController> logger)
    {
        _userManager = userManager;
        _db = db;
        _config = config;
        _logger = logger;
    }

    [HttpPost("api/revenuecat-webhook")]
    public async Task<IActionResult> Handle()
    {
        // 1. Verify the shared secret. RevenueCat sends it exactly as entered
        //    in the dashboard, conventionally "Bearer <secret>".
        var expected = _config["RevenueCat:WebhookAuth"];
        var received = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(expected) ||
            !string.Equals(received, $"Bearer {expected}", StringComparison.Ordinal))
        {
            return Unauthorized();
        }

        JsonDocument doc;
        try
        {
            doc = await JsonDocument.ParseAsync(Request.Body);
        }
        catch
        {
            return BadRequest("Invalid JSON.");
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("event", out var ev))
            {
                return Ok();
            }

            var type = ev.TryGetProperty("type", out var t) ? t.GetString() : null;

            // The dashboard's "Send test event" button — answer 200 so the
            // integration check passes.
            if (type == "TEST")
            {
                _logger.LogInformation("RevenueCat webhook test event received.");
                return Ok();
            }

            // Consumable credit packs arrive as INITIAL_PURCHASE (first time)
            // or NON_RENEWING_PURCHASE (repeat buys). Everything else
            // (renewals, cancellations, expirations...) doesn't credit balance.
            if (type is not ("INITIAL_PURCHASE" or "NON_RENEWING_PURCHASE"))
            {
                return Ok();
            }

            var appUserId    = ev.TryGetProperty("app_user_id",    out var u) ? u.GetString() : null;
            var productId    = ev.TryGetProperty("product_id",     out var p) ? p.GetString() : null;
            var transaction  = ev.TryGetProperty("transaction_id", out var x) ? x.GetString() : null;
            var environment  = ev.TryGetProperty("environment",    out var e) ? e.GetString() : null;

            if (string.IsNullOrEmpty(appUserId) || string.IsNullOrEmpty(productId))
            {
                _logger.LogWarning("RevenueCat webhook missing app_user_id/product_id.");
                return Ok();
            }

            if (!CreditPacks.TryGetValue(productId, out var amount))
            {
                _logger.LogWarning("Unknown product_id {ProductId} — no balance credited.", productId);
                return Ok();
            }

            // 2. Idempotency: RevenueCat retries webhooks on failure and can
            //    deliver duplicates. The Apple transaction ID is stored in
            //    Payment.PaymentProof — if we've seen it before, this delivery
            //    is a duplicate and the balance was already credited.
            if (!string.IsNullOrEmpty(transaction) &&
                await _db.Payments.AnyAsync(pay => pay.PaymentProof == transaction))
            {
                _logger.LogInformation("Duplicate webhook for transaction {Transaction} — already credited.", transaction);
                return Ok();
            }

            // 3. Find the user. The app sets RevenueCat's app user ID to the
            //    account email (or the userId if /login ever returns one), so
            //    try email first, then raw id.
            var user = await _userManager.FindByEmailAsync(appUserId)
                    ?? await _userManager.FindByIdAsync(appUserId);

            if (user == null)
            {
                // Return Ok anyway: a 4xx/5xx makes RevenueCat retry forever.
                _logger.LogWarning("RevenueCat webhook: no user found for {AppUserId}.", appUserId);
                return Ok();
            }

            // 4. Credit the balance row (create it if this is the first purchase).
            var balance = await _db.Balances.FirstOrDefaultAsync(b => b.UserId == user.Id);
            if (balance == null)
            {
                balance = new Balance
                {
                    UserId = user.Id,
                    CurrentBalance = 0m,
                    PaidMoneyBefore = false,
                };
                _db.Balances.Add(balance);
            }

            balance.CurrentBalance += amount;
            balance.PaidMoneyBefore = true;

            // 5. Record the payment. PaymentProof = Apple transaction ID, which
            //    is what the idempotency check above keys on. One SaveChanges
            //    makes the credit + record atomic.
            _db.Payments.Add(new Payment
            {
                UserId = user.Id,
                PaymentDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                PaymentProof = transaction ?? $"no-transaction-id:{Guid.NewGuid()}",
                PaymentAmount = amount,
            });

            await _db.SaveChangesAsync();

            _logger.LogInformation(
                "Credited {Amount} to user {UserId} (product {ProductId}, tx {Transaction}, env {Environment}).",
                amount, user.Id, productId, transaction, environment);

            // Always 200 once authenticated: non-2xx triggers RevenueCat retries.
            return Ok();
        }
    }
}
