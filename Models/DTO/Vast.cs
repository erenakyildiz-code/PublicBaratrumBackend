    
using System.Text.Json.Serialization; 
    public record VastSearchResponse(
        [property: JsonPropertyName("offers")] List<VastOffer> Offers);

    public record VastOffer(
        [property: JsonPropertyName("id")]         long   Id,
        [property: JsonPropertyName("gpu_name")]   string GpuName,
        [property: JsonPropertyName("dph_total")]  double DphTotal,
        [property: JsonPropertyName("geolocation")] string? Geolocation);

    public record SessionQuote(
        double PricePerHour, double OurCostPerHour,
        long BrainOfferId, string BrainGpu,
        long AudioOfferId, string AudioGpu,
        DateTime QuotedAtUtc);