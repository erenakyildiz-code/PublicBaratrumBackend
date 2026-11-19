using System.Net.Http.Headers;   

public class VastPricingService
{
    private readonly HttpClient _http;
    private const double Markup = 2.0; // your 0.5 -> 1.0 logic

    // GPU tiers you're willing to accept, cheapest-first via API sort
    private static readonly string[] BrainGpus  = { "RTX 5090" };
    private static readonly string[] AudioGpus  = { "RTX 5070", "RTX 4070 Ti SUPER", "RTX 3090" };

    public VastPricingService(HttpClient http, IConfiguration config)
    {
        _http = http;
        _http.BaseAddress = new Uri("https://console.vast.ai/api/v0/");
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", config["Vast:ApiKey"]);
    }

    public async Task<SessionQuote?> GetSessionQuoteAsync()
    {
        var brain = await GetCheapestOfferAsync(BrainGpus);
        var audio = await GetCheapestOfferAsync(AudioGpus);

        if (brain is null || audio is null) return null;

        var costPerHour = brain.DphTotal + audio.DphTotal;
        var userPrice   = Math.Ceiling(costPerHour * Markup * 100) / 100; // round up to cents

        return new SessionQuote(
            PricePerHour:      userPrice,
            OurCostPerHour:    costPerHour,
            BrainOfferId:      brain.Id,
            BrainGpu:          brain.GpuName,
            AudioOfferId:      audio.Id,
            AudioGpu:          audio.GpuName,
            QuotedAtUtc:       DateTime.UtcNow
        );
    }

    private async Task<VastOffer?> GetCheapestOfferAsync(string[] gpuNames)
    {
        var body = new
        {
            gpu_name  = new { @in = gpuNames },
            num_gpus  = new { eq = 1 },
            verified  = new { eq = true },
            rentable  = new { eq = true },
            order     = new[] { new[] { "dph_total", "asc" } },
            type      = "on-demand",
            limit     = 1
        };

        var resp = await _http.PostAsJsonAsync("bundles/", body);
        resp.EnsureSuccessStatusCode();

        var result = await resp.Content.ReadFromJsonAsync<VastSearchResponse>();
        return result?.Offers?.FirstOrDefault();
    }

}