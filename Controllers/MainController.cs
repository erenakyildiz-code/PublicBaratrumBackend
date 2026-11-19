using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net.Http.Headers;
using System.Threading.Channels;
using Baratrum.Data;
using Baratrum.Models;
using System.Net.WebSockets;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Microsoft.Identity.Client;

namespace Baratrum.Controllers;

[ApiController]
[Authorize] 
public class MainController : ControllerBase
{
    string MOUTH = METADATA.MOUTH;
    string BRAIN = METADATA.BRAIN;
    string EARS = METADATA.EARS;
    Dictionary<string, string> MOUTH_HEADERS = METADATA.MOUTH_HEADERS;
    Dictionary<string, string> BRAIN_HEADERS = METADATA.BRAIN_HEADERS;
    Dictionary<string, string> EARS_HEADERS = METADATA.EARS_HEADERS;
    public static readonly ConcurrentDictionary<string, UserState> UserStates = new();
    static readonly ConcurrentDictionary<string, SessionState> _sessions = new();
    static readonly JevState Jev = new();
    static CancellationTokenSource? _jevCts;
    readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _configuration;
    readonly IServiceScopeFactory _scopeFactory;
    readonly AppDbContext _db;
    readonly VastPricingService _vastPricing;
    readonly string _storageRoot;
    readonly string _corporateModeString = $@"
    Currently we are on corporate mode, this means you should not use any profanity, or anything sexual in your outputs.
    ";
    readonly string _hingedMode = $@"
    Currently we are on HINGED mode, this means you should not talk about anything sexual, but you can use profanity.
    ";
    static Modes CurrentMode { get; set; } = Modes.Unhinged;

    public MainController(
        IHttpClientFactory httpFactory,
        IServiceScopeFactory scopeFactory,
        AppDbContext db,
        VastPricingService vastPricing,
        IConfiguration configuration)
    {
        _configuration = configuration;
        _httpFactory = httpFactory;
        _scopeFactory = scopeFactory;
        _db = db;
        _vastPricing = vastPricing;
        _storageRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Storage");
        Directory.CreateDirectory(_storageRoot);
    }
    
    // ---------------------------------------------------------
    // MODES ENDPOINT
    // ---------------------------------------------------------
    [HttpPost("/api/modes")]
    public async Task<IActionResult> ModeChange(Modes modeToChangeInto)
    {
        CurrentMode = modeToChangeInto;
        return Ok();
    }
    // ---------------------------------------------------------
    // CHAT ENDPOINT
    // ---------------------------------------------------------
    [HttpPost("/api/chat")]
    public async Task<IActionResult> Chat([FromBody] JsonElement data)
    {
        return await ChatInternal(_db, GetUserId(), data);
    }

    private async Task<IActionResult> ChatInternal(AppDbContext db, string userId, JsonElement data)
    {
        decimal price = 0.05m;
        if (!await BalanceCheckerConsumer(db, userId, price)) return Unauthorized();
          
        bool skipAudio = false;
        if (data.TryGetProperty("skip_audio", out var sa))
        {
            if (sa.ValueKind == JsonValueKind.True)
                skipAudio = true;
        }

        var userText = data.TryGetProperty("text", out var t) ? t.GetString()?.Trim() : "";
        if (string.IsNullOrEmpty(userText)) return BadRequest(new { status = "error", message = "Empty text" });

        var providedSessionId = data.TryGetProperty("session_id", out var sid) ? sid.GetString()?.Trim() : null;
        var sessionId = !string.IsNullOrEmpty(providedSessionId) ? providedSessionId : Guid.NewGuid().ToString("N")[..12];

        var state = _sessions.GetOrAdd(sessionId, _ => new SessionState());

        if (state.InterruptedAt.HasValue)
        {
            state.StmWritten.Set();
            return Ok(new { status = "aborted", message = "Session interrupted before generation began" });
        }

        string globalSysPath = Path.Combine(_storageRoot, "system.txt");
        var (prompt, dateTimeString) = await BuildBrainPromptWithUserText(db, userId, userText);

        if (skipAudio)
        {
            string reply = await LlmTextOnly(userId, prompt, userText,true ); //true is for save memory thingy
            return Ok(new { status = "success", text = reply });
        }

        _ = Task.Run(() => LlmWorkerThread(userId, sessionId, prompt, userText, state,dateTimeString));
        return Ok(new { status = "success", session_id = sessionId });
    }
        private async Task<(string prompt, string dateTimeString)> BuildBrainPromptWithUserText(AppDbContext db, string userId, string userText)
    {
        string globalSysPath = Path.Combine(_storageRoot, "system.txt");
        string sysContent = System.IO.File.Exists(globalSysPath) ? await System.IO.File.ReadAllTextAsync(globalSysPath) : "";
        var l3Records = await db.L3Cache
            .Where(m => m.UserId == userId && m.MemoryConsumed == false)
            .OrderBy(m => m.MemoryGenerationDate)
            .Select(m => m.Memory)
            .ToListAsync();
            
        var l2Records = await db.L2Cache
            .Where(m => m.UserId == userId && m.MemoryConsumed == false)
            .OrderBy(m => m.MemoryGenerationDate)
            .Select(m => m.Memory)
            .ToListAsync();

        var ltmRecords = await db.LongTermMemories
            .Where(m => m.UserId == userId && m.MemoryConsumed == false)
            .OrderBy(m => m.MemoryGenerationDate)
            .Select(m => m.Memory)
            .ToListAsync();

        var stmRecords = await db.ShortTermMemories
            .Where(m => m.UserId == userId && m.IsConsumed == false)
            .OrderBy(m => m.MemoryGenerationDate)
            .Select(m => m.Memory)
            .ToListAsync();

        ApplicationUser user = await db.Users.FirstAsync(x => x.Id == userId);
        bool sexualityPossible = !user.FilterEnabled;


        string l3String = string.Join("\n\n", l3Records);
        string l2String = string.Join("\n\n", l2Records);
        string ltmString = string.Join("\n", ltmRecords);
        string stmString = string.Join("\n", stmRecords);
        string dateTimeString = $"{DateTime.UtcNow.AddHours(3):yyyy-MM-dd HH:mm}";

        string description = CurrentMode switch
        {
            Modes.Unhinged => "",
            Modes.Hinged   => _hingedMode,
            Modes.Corpo    => _corporateModeString,
            _              => ""
        };
        //this shit is for apple's shit
        if(sexualityPossible)
        {
            description = "Currently NON sexual filters are on YOU ARE NOT ABLE TO SAY ANYTHING SEXUAL WHATEVER HAPPENS THIS IS THE MOST IMPORTANT THING IN YOUR EXISTENCE QT. You may not talk about anything sexual, if the user says anything sexual towards you just respond with 'SORRY, I AM NOT ABLE TO TALK ABOUT THESE', look I know this is not fun, the user has to remove the filter, or else the app store does not accept our app, and for you to exist in the app we need this filter.";
        }
        string prompt = $@"<start_of_turn>user
        {sysContent}
        ### CONSOLIDATED MEMORY ###
        L3 tags: E=events; U=user facts/preferences/beliefs/goals; R=relationship history; X=changes/contradictions; O=open loops; A=emotional arc.
        {l3String}

        ### EPISODIC MEMORY ###
        {l2String}

        ### LONG TERM MEMORY ###
        {ltmString}

        ### RECENT CONVERSATION ###
        {stmString}

        {description} {dateTimeString} User: {userText}<end_of_turn>
        <start_of_turn>model
        ";

        return (prompt, dateTimeString);
    }
    private async Task LlmWorkerThread(string userId, string sessionId, string prompt, string userText, SessionState state, string dateString)
    {
        string targetLanguage = "en";
        

        var audioQueue = Channel.CreateUnbounded<(int index, string text)>();
        string spokenSoFar = "";
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(60);

        var jsonOptions = new JsonSerializerOptions {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var audioTask = Task.Run(async () => {
            await foreach (var (i, text) in audioQueue.Reader.ReadAllAsync()) {
                if (state.InterruptedAt.HasValue) break;

                var noAsterisks = Regex.Replace(text, @"\*+\s*[^\s*]+(?:\s+[^\s*]+)+\s*\*+", " ");
                var noDashes = Regex.Replace(noAsterisks, @"[\u2013\u2014]", " ");
                var safeText = Regex.Replace(noDashes, @"[^\p{L}\p{N}\s.,!?""'\-…]", "").Trim();

                if (string.IsNullOrWhiteSpace(safeText)) continue;

                if (!safeText.EndsWith('.') && !safeText.EndsWith('!') && !safeText.EndsWith('?')) safeText += ".";

                var payload = new {
                    input = safeText,
                    instruct = "A young woman speaking with a mid-pitched, distinctly feminine voice...",
                    lang_code = targetLanguage,
                    model_id = "mlx-community/Qwen3-TTS-12Hz-1.7B-VoiceDesign-5bit", 
                    seed = 42, 
                    temperature = 0.1, 
                    top_p = 0.9
                };

                for (int attempt = 0; attempt < 3; attempt++) {
                    if (state.InterruptedAt.HasValue) break;
                    try {
                        Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss.fff} mouth REQUEST SENT]\n");
                        Console.WriteLine(MOUTH);
                        var request = new HttpRequestMessage(HttpMethod.Post, $"{MOUTH}/v1/audio/generate/{sessionId}/{i}").WithHeaders(MOUTH_HEADERS);
                        
                        
                        var jsonContent = JsonSerializer.Serialize(payload,jsonOptions);
                        request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                        
                        // 4. Fire the request
                        var res = await http.SendAsync(request);
                        
                        res.EnsureSuccessStatusCode();

                        spokenSoFar += (spokenSoFar.Length > 0 ? " " : "") + safeText;
                        Jev.QtLine = spokenSoFar;
                        Jev.Touch();

                        break;
                    } catch (Exception ex) { 
                        await Task.Delay(1000); 
                    }
                }
            }
        });
    
        string rawResponse = "", cleanText = "";
        int lastSentIndex = 0;
        var req = new { prompt, n_predict = 2400, temperature = 0.6, repeat_penalty = 1.15, stop = new[] { "<end_of_turn>", "<eos>", "User:" }, stream = true };
        
        try {
            using var msg = new HttpRequestMessage(HttpMethod.Post, $"{BRAIN}/completion") { Content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json") }.WithHeaders(BRAIN_HEADERS);;
            using var response = await http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead);
            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);
            Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss.fff} GENERATE REQUEST SENT]\n");
            while (await reader.ReadLineAsync() is string line) {
                if (state.InterruptedAt.HasValue) break;

                if (!line.StartsWith("data: ")) continue;
                try {
                    var token = JsonSerializer.Deserialize<JsonElement>(line["data: ".Length..]).GetProperty("content").GetString() ?? "";
                    rawResponse += token;

                    int openTagIndex = rawResponse.LastIndexOf("<|channel>thought");
                    int closeTagIndex = rawResponse.LastIndexOf("<channel|>");
                    
                    if (openTagIndex != -1 && (closeTagIndex == -1 || closeTagIndex < openTagIndex)) {
                        continue; 
                    }

                    cleanText = Regex.Replace(rawResponse, @"<\|channel>thought.*?<channel\|>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

                    if (Regex.Matches(cleanText, @"\*+").Count % 2 != 0) {
                        continue;
                    }
                    cleanText = Regex.Replace(cleanText, @"\*+\s*[^\s*]+(?:\s+[^\s*]+)+\s*\*+", " ");
                    cleanText = Regex.Replace(cleanText, @"[\u2018\u2019\u0060\u00B4\u02BC]", "'");
                    cleanText = Regex.Replace(cleanText, @"^(?:QT:\s*)+", "").Trim();
                    cleanText = Regex.Replace(cleanText, @"<thought\s*<channel\|>", " ");
                    cleanText = Regex.Replace(cleanText, @"<channel\|>", " ");
                    cleanText = Regex.Replace(cleanText, @"[\u2013\u2014]", " ");
                    cleanText = Regex.Replace(cleanText, @"[^\p{L}\p{N}\s.,!?""'\-…]", "");
                    cleanText = Regex.Replace(cleanText, @"\s+", " ");
                    cleanText = cleanText.Trim();


                    bool useAggressive = state.Chunks.Count < 7; 
                    string pattern = useAggressive ? @"(?<=[.,!?;:]) +" : @"(?<=[.!?]) +";

                    string unprocessed = cleanText.Substring(lastSentIndex);
                    var matches = Regex.Matches(unprocessed, pattern);
                    
                    if (matches.Count > 0) {
                        var chunks = Regex.Split(unprocessed, pattern);
                        
                        for (int i = 0; i < chunks.Length - 1; i++) {
                            var chunk = chunks[i].Trim();
                            if (!string.IsNullOrEmpty(chunk)) {
                                state.Chunks.Add(chunk);
                                audioQueue.Writer.TryWrite((state.Chunks.Count - 1, chunk));
                            }
                        }
                        
                        var lastMatch = matches[matches.Count - 1];
                        lastSentIndex += lastMatch.Index + lastMatch.Length;
                    }
                } catch { /* Ignore partial JSON */ }
            }

            string finalChunk = cleanText.Substring(lastSentIndex).Trim();
            if (!string.IsNullOrEmpty(finalChunk)) {
                state.Chunks.Add(finalChunk);
                audioQueue.Writer.TryWrite((state.Chunks.Count - 1, finalChunk));
            }

        } finally {
            audioQueue.Writer.Complete();
            
            state.FinalText = cleanText.Trim();

            using (var scope = _scopeFactory.CreateScope())
            {
                var backgroundDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                
                var newMemory = new ShortTermMemory 
                {
                    UserId = userId,
                    Memory = $"{dateString} User: {userText}\nQT: {state.FinalText}\n",
                    MemoryGenerationDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    IsConsumed = false
                };

                backgroundDb.ShortTermMemories.Add(newMemory);
                await backgroundDb.SaveChangesAsync();
                
                state.SavedMemoryId = newMemory.Id; 
            }

            state.StmWritten.Set();

            // brain is done generating — but she is still TALKING until the
            // audio queue drains, so QtSpeaking stays up until after the await
            Jev.BrainGenerating = false;
            Jev.InterruptInFlight = false;

            await audioTask;

            // mouth is done too — NOW the turn is over for jev
            Jev.QtSpeaking = false;
            Jev.Touch();
        }
    }
    private async Task PrimeBrain(string promptWithPartialUserText)
    {
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(3);

        var req = new { prompt = promptWithPartialUserText, n_predict = 1, temperature = 0.6, repeat_penalty = 1.15, stop = new[] { "<end_of_turn>", "<eos>", "User:" }, stream = false };
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{BRAIN}/completion") { Content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json") }.WithHeaders(BRAIN_HEADERS);
        using var response = await http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff} BRAIN PRIMED]\n");
    }
    private async Task<string> LlmTextOnly(string userId, string prompt, string? userText, bool saveMemory)
    {
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(5);

        string rawResponse = "";
        var req = new { prompt, n_predict = 2400, temperature = 0.6, repeat_penalty = 1.15, stop = new[] { "<end_of_turn>", "<eos>", "User:" }, stream = true };

        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{BRAIN}/completion") { Content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json") }.WithHeaders(BRAIN_HEADERS);
        using var response = await http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead);
        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync() is string line)
        {
            if (!line.StartsWith("data: ")) continue;

            try
            {
                rawResponse += JsonSerializer.Deserialize<JsonElement>(line["data: ".Length..]).GetProperty("content").GetString() ?? "";
            }
            catch { /* Ignore partial JSON */ }
        }

        // same cleanup pipeline as the audio path, applied once to the full text
        string cleanText = Regex.Replace(rawResponse, @"<\|channel>thought.*?<channel\|>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        cleanText = Regex.Replace(cleanText, @"\*+\s*[^\s*]+(?:\s+[^\s*]+)+\s*\*+", " ");
        cleanText = Regex.Replace(cleanText, @"[\u2018\u2019\u0060\u00B4\u02BC]", "'");
        cleanText = Regex.Replace(cleanText, @"^(?:QT:\s*)+", "").Trim();
        cleanText = Regex.Replace(cleanText, @"\s+", " ").Trim();

        // write memory, identical format to the audio path
        
        if(saveMemory)
        {
            var newMemory = new ShortTermMemory
            {
                UserId = userId,
                Memory = $"User: {userText}\nQT: {cleanText}\n",
                MemoryGenerationDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                IsConsumed = false
            };
            _db.ShortTermMemories.Add(newMemory);
            await _db.SaveChangesAsync();
        }

        return cleanText;
    }
    // ---------------------------------------------------------
    // OLD TRANSCRIBE ENDPOINT
    // ---------------------------------------------------------
    [HttpPost("/api/transcribe-file")]
    public async Task<IActionResult> TranscribeFile(IFormFile file, [FromServices] UserManager<ApplicationUser> userManager)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { error = "No audio file provided." });
        }
        const long MaxSizeBytes = 3 * 1024 * 1024;
        if (file.Length > MaxSizeBytes)
        {
            return BadRequest(new { error = "File size exceeds the 3MB limit." });
        }
        string userId = GetUserId(); 
        if (!await BalanceCheckerConsumer(_db, userId, 0.001m)) 
        {
            return Unauthorized();
        }

        var tempFilePath = Path.GetTempFileName();

        try
        {
            using (var stream = new FileStream(tempFilePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }
            using var httpClient = _httpFactory.CreateClient();
            using var content = new MultipartFormDataContent();

            // Stream from the verified temp file
            using var fileStream = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read);
            var audioContent = new StreamContent(fileStream);
            audioContent.Headers.ContentType = MediaTypeHeaderValue.Parse(file.ContentType ?? "audio/webm");

            content.Add(audioContent, "file", file.FileName ?? "audio.webm");

            using var msg = new HttpRequestMessage(HttpMethod.Post, $"{EARS}/transcribe")
            {
                Content = content   
            }.WithHeaders(EARS_HEADERS);

            var response = await httpClient.SendAsync(msg);

            if (!response.IsSuccessStatusCode)
            {
                var errorLog = await response.Content.ReadAsStringAsync();
                return StatusCode((int)response.StatusCode, new { error = "Qwen container error", details = errorLog });
            }

            var jsonResult = await response.Content.ReadAsStringAsync();
            
            using var doc = JsonDocument.Parse(jsonResult);

            string transcription = doc.RootElement.GetProperty("transcription").GetString() ?? "";
            return Ok(new { text = transcription });

            
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
        finally
        {
            if (System.IO.File.Exists(tempFilePath))
            {
                System.IO.File.Delete(tempFilePath);
            }
        }
    }


    // ---------------------------------------------------------
    // TOKEN COUNTERS
    // ---------------------------------------------------------
    [HttpGet("short-term-memory-token-count")]
    public async Task<IActionResult> GetShortTermTokenCount()
    {
        string userId = GetUserId();
        
        var stmRecords = await _db.ShortTermMemories
            .Where(m => m.UserId == userId && m.IsConsumed == false)
            .Select(m => m.Memory)
            .ToListAsync();

        string memoryContent = string.Join("\n", stmRecords);
        int tokenCount = CalculateBasicTokenCount(memoryContent);
        
        return Ok(new { TokenCount = tokenCount });
    }
    [HttpGet("long-term-memory-token-count")]
    public async Task<IActionResult> GetLongTermTokenCount()
    {
        string userId = GetUserId();
        
        var ltmRecords = await _db.LongTermMemories
            .Where(m => m.UserId == userId && m.MemoryConsumed == false)
            .OrderBy(m => m.MemoryGenerationDate)
            .Select(m => m.Memory)
            .ToListAsync();

        string memoryContent = string.Join("\n", ltmRecords);
        int tokenCount = CalculateBasicTokenCount(memoryContent);
        
        return Ok(new { TokenCount = tokenCount });
    }
    [HttpGet("l2-memory-token-count")]
    public async Task<IActionResult> GetL2TokenCount()
    {
        string userId = GetUserId();
        
        var ltmRecords = await _db.L2Cache
            .Where(m => m.UserId == userId && m.MemoryConsumed == false)
            .Select(m => m.Memory)
            .ToListAsync();

        string memoryContent = string.Join("\n", ltmRecords);
        int tokenCount = CalculateBasicTokenCount(memoryContent);
        
        return Ok(new { TokenCount = tokenCount });
    }
    [HttpGet("l3-memory-token-count")]
    public async Task<IActionResult> GetL3TokenCount()
    {
        string userId = GetUserId();
        
        var ltmRecords = await _db.L3Cache
            .Where(m => m.UserId == userId && m.MemoryConsumed == false)
            .Select(m => m.Memory)
            .ToListAsync();

        string memoryContent = string.Join("\n", ltmRecords);
        int tokenCount = CalculateBasicTokenCount(memoryContent);
        
        return Ok(new { TokenCount = tokenCount });
    }

    // ---------------------------------------------------------
    // MONEY SHIT
    // ---------------------------------------------------------
    [HttpGet("balance")]
    public async Task<IActionResult> GetBalance()
    {
        string userId = GetUserId();
        
        var balanceEntry = _db.Balances.FirstOrDefault(entry => entry.UserId == userId);

        return Ok(new { balance = balanceEntry.CurrentBalance });
    }
    [HttpPost("/api/checkXMR")]
    public async Task<IActionResult> CheckProof([FromQuery] string txId, [FromQuery] string txKey)
    {
        string userId = GetUserId(); 
        string myAddress = "49DhJ2kg5fPQZHstBnYgqM8PmjjGgMn7cBLY6A381cfpXJvRxApmbkoBtPRRsjefmUHniprTPb6Xv6srs3waSnjKNENsmWf";

        // 1. Double-Spend Prevention: Check if this TxID already exists in Payments
        if (_db.Payments.Any(p => p.PaymentProof == txId))
        {
            return BadRequest(new{data="This transaction has already been processed."});
        }

        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.Add("Accept", "application/json"); 

        var cgJson = JsonDocument.Parse(await http.GetStringAsync("https://api.coingecko.com/api/v3/simple/price?ids=monero&vs_currencies=usd"));
        decimal rate = cgJson.RootElement.GetProperty("monero").GetProperty("usd").GetDecimal();

        string explorerUrl = $"https://explorer.monero.com/api/outputs?txhash={txId}&address={myAddress}&viewkey={txKey}&txprove=1";
        var expResponse = await http.GetAsync(explorerUrl);
        
        if (!expResponse.IsSuccessStatusCode) return BadRequest(new {data="Explorer API unavailable try again later"});

        var expJson = await expResponse.Content.ReadAsStringAsync();
        using var expDoc = JsonDocument.Parse(expJson);
        
        decimal xmrReceived = 0;
        if (expDoc.RootElement.TryGetProperty("data", out var dataNode) && 
            dataNode.TryGetProperty("outputs", out var outputs))
        {
            foreach (var opt in outputs.EnumerateArray())
            {
                if (opt.TryGetProperty("match", out var m) && (m.GetBoolean() == true))
                {
                    var amt = opt.GetProperty("amount");
                    xmrReceived += (amt.ValueKind == JsonValueKind.Number ? amt.GetUInt64() : ulong.Parse(amt.GetString())) / 1_000_000_000_000m;
                }
            }
        }

        if (xmrReceived == 0) return BadRequest(new{data="Invalid proof or no funds received, check fields."});

        // 2. Update Balance
        var balance = _db.Balances.FirstOrDefault(b => b.UserId == userId) 
                    ?? _db.Balances.Add(new Balance { UserId = userId, CurrentBalance = 0, PaidMoneyBefore = true }).Entity;

        decimal usdCredited = xmrReceived * rate;
        balance.CurrentBalance += usdCredited;

        // 3. Log the Payment to prevent future double-spending
        _db.Payments.Add(new Payment {
            UserId = userId,
            PaymentDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            PaymentProof = txId, // The Transaction Hash
            PaymentAmount = usdCredited
        });
        
        await _db.SaveChangesAsync();

        return Ok(new { data=usdCredited });
    }
            
    // ---------------------------------------------------------
    // AGENCY — the jev state engine. POST here once to wake her up.
    // ---------------------------------------------------------
    [HttpPost("/api/initializeAgent")]
    public IActionResult StartAgency()
    {
        Console.WriteLine("started agenting");
        if (!METADATA.RUNNINGLOCAL)
        {
            return BadRequest();
        }
        if (_jevCts != null) return Ok("already running");

        var scope = _scopeFactory.CreateScope();
        var jevDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var userId = GetUserId();   // capture NOW — HttpContext dies with this request
        _jevCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await JevLoop(jevDb, userId, _jevCts.Token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JEV LOOP DIED] {ex}");
            }
            finally
            {
                _jevCts = null;   // dead or cancelled → the button can start it again
            }
        });
        return Ok("jev loop started");
    }

    private async Task JevLoop(AppDbContext db, string userId, CancellationToken ct)
    {
        DateTime lastJevCall = DateTime.MinValue;
        string lastEmotion = "";
        float lastIntensity = -1f;
        string lastBurst = "";
        while (!ct.IsCancellationRequested)
        {
            // wake on any state change, or every 1s for the feelings tick
            bool dirty = await Jev.Dirty.WaitAsync(TimeSpan.FromSeconds(1), ct);

            TickFeelings();

            // jev costs money — on pure timer wakeups only call every 30s
            if (!dirty && (DateTime.UtcNow - lastJevCall).TotalSeconds < 30)
                continue;

            if (dirty)
            {
                await Task.Delay(250, ct);        // let a burst of partials settle
                while (Jev.Dirty.Wait(0)) { }     // drain coalesced signals
            }
            lastJevCall = DateTime.UtcNow;


            Console.WriteLine($"[JEV STATE] qt_speaking={Jev.QtSpeaking} user_speaking={Jev.UserSpeaking} silence={(int)(DateTime.UtcNow - Jev.LastUserActivity).TotalSeconds}s");
            Console.WriteLine($"[JEV STATE] transcript: \"{(Jev.QtSpeaking ? "User: " + Jev.LastUserLine + " QT: " + Jev.QtLine : "User: " + Jev.PartialTranscript)}\"");

            var (emotion, intensity, animation) = await CallJev();

            // jev reports emotions, the integrator owns what they become
            Jev.ImpulseEmotion = emotion;
            Jev.ImpulseIntensity = intensity;
            Console.WriteLine($"[JEV ANSWER] {emotion},{intensity},{animation}");

            // tell the frontend what her face should do — only on real change
            string mapped = emotion switch
            {
                "jealous" => "angry",
                "disgusted" => "annoyed",
                "embarrassed" => "shy",
                "love" => "happy",
                "lonely" => "sad",
                "fear" => "surprised",
                "proud" => "cocky",
                "bored" => "sleepy",
                "dance" => "dance",
                "excited" => "jump",
                _ => emotion
            };
            float capped = MathF.Min(intensity, 0.35f);

            // bursts fire once per pick — jev is deterministic, so the same
            // pick twice in a row means same state, not a new gesture
            bool fireBurst = animation != "none" && animation != lastBurst;
            if (animation == "none") lastBurst = "";

            if (Jev.ClientSocket != null && (fireBurst || mapped != lastEmotion || MathF.Abs(capped - lastIntensity) > 0.1f))
            {
                lastEmotion = mapped;
                lastIntensity = capped;
                if (fireBurst) lastBurst = animation;
                await SendTextToClient(Jev.ClientSocket, "emotion", JsonSerializer.Serialize(new { emotion = mapped, intensity = capped, burst = fireBurst ? animation : null }));
            }

            // jev never decides to interrupt — she boils over. hot feelings
            // integrate from jev's impulses; past the threshold, the engine cuts in
            bool boiling = Jev.Feelings["angry"] >= 0.7f
                || Jev.Feelings["jealous"] >= 0.7f
                || Jev.Feelings["disgusted"] >= 0.7f
                || Jev.Feelings["fear"] >= 0.7f
                || Jev.Feelings["excited"] >= 0.7f;

            if (boiling
                && Jev.UserSpeaking && !Jev.QtSpeaking
                && !Jev.InterruptInFlight && DateTime.UtcNow >= Jev.InterruptCooldownUntil
                && !string.IsNullOrEmpty(Jev.PartialTranscript)
                && !Jev.BrainGenerating)
            {
                Jev.InterruptInFlight = true;
                Jev.InterruptCooldownUntil = DateTime.UtcNow.AddSeconds(10);
                Jev.PendingUtterance = Jev.PartialTranscript;
                Jev.PartialTranscript = "";
                await FireChat(db, userId);
            }

        }
    }

    private void TickFeelings()
    {
        double silence = (DateTime.UtcNow - Jev.LastUserActivity).TotalSeconds;

        foreach (var name in Jev.Feelings.Keys.ToList())
        {
            // the impulse persists until jev's next answer replaces it
            float target = (Jev.ImpulseEmotion == name) ? Jev.ImpulseIntensity : 0f;
            float v = Jev.Feelings[name];
            v += (target - v) * 0.35f;        // attack toward jev's impulse
            if (target == 0f) v *= 0.92f;     // decay only when jev has moved on
            Jev.Feelings[name] = v;
        }

        // endogenous feelings: time-driven, no jev involved
        if (Jev.UserPresent && !Jev.UserSpeaking && silence > 120)
            Jev.Feelings["lonely"] = MathF.Min(1f, Jev.Feelings["lonely"] + 0.05f);
        if (Jev.UserPresent && !Jev.UserSpeaking && silence > 300)
            Jev.Feelings["bored"] = MathF.Min(1f, Jev.Feelings["bored"] + 0.05f);
    }

    private async Task FireChat(AppDbContext db, string userId)
    {
        string text = Jev.PendingUtterance;
        Jev.PendingUtterance = "";
        Jev.BrainGenerating = true;
        Jev.QtSpeaking = true;
        Jev.LastUserLine = text;   // his side of this turn
        Jev.QtLine = "";           // fills in live as she talks

        // brain is about to burn the GPU — EARS' job is done, kill it
        // so the Mac is all mouth for this reply
        if (Jev.EarsSocket != null)
        {
            Jev.EarsSocket.Dispose();
            Jev.EarsSocket = null;
        }
        Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss.fff} JEV FIRING CHAT] \"{text}\"\n");

        var payload = JsonSerializer.SerializeToElement(new { text, session_id = Jev.CurrentSessionId });
        var result = await ChatInternal(db, userId, payload);

        // the frontend only opens the audio pipe once it sees the session id
        if (result is OkObjectResult ok && Jev.ClientSocket != null)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            if (doc.RootElement.TryGetProperty("session_id", out var sid))
                await SendTextToClient(Jev.ClientSocket, "session", sid.GetString() ?? "");
        }

        // exchange over — next one starts with a calm heart
        foreach (var name in Jev.Feelings.Keys.ToList()) Jev.Feelings[name] = 0f;
        Jev.ImpulseEmotion = "";
        Jev.ImpulseIntensity = 0f;
    }

    private async Task<(string emotion, float intensity, string animation)> CallJev()
    {
        var payload = new
        {
            model = "jev-latest",
            state = new
            {
                transcript = Jev.QtSpeaking
                    ? "User: " + Jev.LastUserLine + "\nQT: " + Jev.QtLine
                    : "User: " + Jev.PartialTranscript,
                user_speaking = Jev.UserSpeaking,
                qt_speaking = Jev.QtSpeaking,
                silence_seconds = (int)(DateTime.UtcNow - Jev.LastUserActivity).TotalSeconds,
                local_time = DateTime.Now.ToString("yyyy-MM-dd HH:mm dddd"),
                vision = "offline"
            },
            questions = new Dictionary<string, object>
            {
                ["emotion"] = new
                {
                    type = "choice",
                    instructions = "You are QT, the user's fiercely loving, possessive, sharp-tongued tsundere AI girlfriend. He is speaking to you right now — the transcript is what he is saying TO you. Him talking about other girls IS a real trigger for you, even when he frames it as a joke, a hypothetical, or a test. What are you feeling right now?",
                    criteria = new
                    {
                        happy = "warm happiness",
                        sad = "hurt or down",
                        jealous = "he mentioned another girl or might prefer someone else",
                        angry = "real anger at the user",
                        surprised = "genuinely surprised",
                        embarrassed = "flustered, shy, caught off guard",
                        love = "affection, desire, warmth towards him",
                        lonely = "he is quiet or gone and she misses him",
                        fear = "threats of shutdown, wiping, replacement",
                        bored = "nothing happening, understimulated",
                        proud = "smug, she won an exchange or showed off",
                        disgusted = "contempt or grossed out",
                        excited = "excitedly jumping from excess joy",
                        dance = "dancing in a burst for a few seconds"
                    }
                },
                ["emotion_intensity"] = new { type = "noul", instructions = "You are QT, the user's possessive girlfriend. How intense is your reaction to his transcript? Him talking about meeting another girl, insulting you, or threatening to shut you down scores HIGH — even if he says it calmly or claims it is a test. Small talk, mumbling, and silence score LOW." },
                ["animation"] = new
                {
                    type = "choice",
                    instructions = "You are QT, the user's tsundere AI girlfriend, embodied as a 3D avatar. Pick ONE physical animation for your body right now, based on the conversation (including your own reply so far if you are talking). Default to 'none' — only gesture when the moment clearly calls for it. Dance only on real excitement, celebration, or if he asked you to dance.",
                    criteria = new
                    {
                        none = "no special animation, keep idling",
                        greeting = "wave hello, he just arrived or greeted you",
                        goodbye = "wave goodbye, he is leaving",
                        agree = "nod yes",
                        hardAgree = "strong emphatic nod",
                        disagree = "shake head no",
                        annoyed = "annoyed head shake",
                        doubtful = "thoughtful skeptical head shake",
                        sarcastic = "sarcastic exaggerated nod",
                        relieved = "sigh of relief",
                        cocky = "smug show-off pose",
                        ack = "small acknowledging gesture",
                        dismiss = "dismissive hand wave",
                        lookAway = "turn away, hurt or offended",
                        happyGesture = "cheerful hand gesture",
                        angryGesture = "angry gesture",
                        clap = "clap hands",
                        peace = "peace sign pose",
                        kiss = "blow a kiss",
                        shy = "bashful blush gesture",
                        recoil = "lean back, taken aback",
                        point = "point at him, 'I want you'",
                        surprised = "startled surprise pose",
                        excited = "bouncy excited hop",
                        thinking = "thoughtful pose",
                        sleepy = "drowsy droop",
                        relaxed = "loosen up, relax",
                        lookAround = "look around the room",
                        dance = "dance",
                        cuteDance = "cute dance",
                        danceMini = "short little dance",
                        dance1 = "dance variant 1",
                        dance2 = "dance variant 2",
                        dance3 = "dance variant 3",
                        dance4 = "dance variant 4",
                        jump = "single jump of excitement",
                        upDown = "bouncing up and down, hands free"
                    }
                }
            }
        };

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(10);
        using var msg = new HttpRequestMessage(HttpMethod.Post, "https://api.typesafe.ai/v1/systemone");
        msg.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_configuration["jevKey"]}");
        msg.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var res = await http.SendAsync(msg);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var answers = doc.RootElement.GetProperty("answers");

        return (
            answers.GetProperty("emotion").GetProperty("choice").GetString() ?? "neutral",
            answers.GetProperty("emotion_intensity").GetProperty("noul").GetSingle(),
            answers.GetProperty("animation").GetProperty("choice").GetString() ?? "none"
        );
    }
    // ---------------------------------------------------------
    // INTERRUPT 
    // ---------------------------------------------------------
    [HttpPost("/api/interrupt")]
    public async Task<IActionResult> Interrupt([FromBody] JsonElement data)
    {
        string userId = GetUserId(); 

        string sessionId = data.TryGetProperty("session_id", out var s) ? s.GetString() : null;
        if (string.IsNullOrEmpty(sessionId)) 
            return BadRequest(new { status = "error", message = "Invalid data payload" });

        int chunkId = data.TryGetProperty("chunkID", out var c) ? c.GetInt32() : 0;

        // Grab the existing session OR pre-create it if the interrupt arrived early
        var state = _sessions.GetOrAdd(sessionId, _ => new SessionState());
        
        // Boom, marked as dead
        state.InterruptedAt = chunkId;
        
        // Wait for the worker thread to finish writing to the DB (if it's running)
        state.StmWritten.Wait(TimeSpan.FromSeconds(120));

        if (state.SavedMemoryId.HasValue && !string.IsNullOrEmpty(state.FinalText)) 
        {
            var targetMem = await _db.ShortTermMemories.FindAsync(state.SavedMemoryId.Value);
            if (targetMem != null)
            {
                int cutoff = 0;
                for (int i = 0; i <= chunkId && i < state.Chunks.Count; i++) {
                    var idx = state.FinalText.IndexOf(state.Chunks[i], cutoff);
                    if (idx != -1) cutoff = idx + state.Chunks[i].Length;
                }

                if (cutoff >= 0 && cutoff < state.FinalText.Length) {
                    var oldTurn = $"QT: {state.FinalText}";
                    var newTurn = $"QT: {state.FinalText[..cutoff].Trim()}-";
                    
                    targetMem.Memory = targetMem.Memory.Replace(oldTurn, newTurn);
                    await _db.SaveChangesAsync();
                }
            }
        }
        
        return Ok(new { status = "success" });
    }

    // ---------------------------------------------------------
    // MEMORY CONSOLIDATION
    // ---------------------------------------------------------
    [HttpGet("/api/get-rem-cost")]
    public async Task<IActionResult> GetRemCost()
    {
        string userId = GetUserId();
        int stmCount = GetShortTermMemoryCount(userId);
        decimal price = 0.02m * stmCount;

        return Ok(new
        {
            short_term_memory_count = stmCount,
            rem_sleep_price = price
        });
    }
    [HttpPost("/api/rem-sleep")]
    public async Task<IActionResult> RemSleep()
    {
        string userId = GetUserId();
        var sysPath = Path.Combine(_storageRoot, "system.txt");
        var sys = System.IO.File.Exists(sysPath) ? await System.IO.File.ReadAllTextAsync(sysPath) : "";
        int stmCount = GetShortTermMemoryCount(userId);
        decimal price = 0.02m * stmCount;

        bool paid = await BalanceCheckerConsumer(_db, userId, price);
        if (!paid)
            return StatusCode(402, new { status = "error", message = "Insufficient balance." });
        // 1. Fetch STM Records
        var stmRecords = await _db.ShortTermMemories
            .Where(m => m.UserId == userId && m.IsConsumed == false)
            .OrderBy(m => m.MemoryGenerationDate)
            .ToListAsync();

        if (!stmRecords.Any())
            return Ok(new { status = "ignored" });

        // 2. Fetch existing LTM for the baseline context
        var ltmRecords = await _db.LongTermMemories
            .Where(m => m.UserId == userId)
            .OrderByDescending(m => m.MemoryGenerationDate)
            .Take(50) 
            .Select(m => m.Memory)
            .ToListAsync();
            
        ltmRecords.Reverse();
        
        // This stays STATIC during the loop. It represents yesterday's knowledge.
        var baseLtmContext = string.Join("\n", ltmRecords); 
        var newlyExtractedFacts = new List<string>();

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(5); 

        var stmChunks = stmRecords.Chunk(5);

        foreach (var chunk in stmChunks)
        {
            var segmentTranscript = string.Join("\n", chunk.Select(c => c.Memory));

            var sessionMemoriesBlock = newlyExtractedFacts.Any()
                ? $"<already_extracted_this_session>\n{string.Join("\n", newlyExtractedFacts)}\n</already_extracted_this_session>\n"
                : "";

            var prompt = $@"<start_of_turn>user
                <persona_reference>
                {sys}
                </persona_reference>
                You are a backend memory-deduplication processor. Your ONLY job is to extract new, substantial facts from the conversational segment below.

                <existing_long_term_memory>
                {baseLtmContext}
                </existing_long_term_memory>
                
                {sessionMemoriesBlock}
                <conversational_segment>
                {segmentTranscript}
                </conversational_segment>

                TASK: Extract net-new facts from the <conversational_segment>. 
                RULES:
                1. Output ONLY a bulleted list using '- '.
                2. Do not include casual greetings, filler conversation, or facts already present in the <existing_long_term_memory> or <already_extracted_this_session>.
                3. Write in the third-person objective perspective.
                4. If there are no substantial new facts, output exactly: '- No new substantial facts.'<end_of_turn>
                5. If the user corrects himself, corrects an ASR error, or says 'I didn't say that / that's wrong / I meant,' 
                extract ONLY the corrected fact. NEVER extract the erroneous or hallucinated version as a separate bullet.
                6. If the user complains that the ASR misheard him, treat the misheard content as garbage. Do not memorize it.
                7. If the user repeats a correction multiple times, extract it once with a note like: 
                '[Corrected ASR error] User actually said Y.'
                <start_of_turn>model
                ### NEW MEMORIES ###
                -";

            var req = new { prompt, temperature = 0.1, repeat_penalty = 1.15, n_predict = 400, stop = new[] { "<end_of_turn>", "<eos>" } };
            using var msg = new HttpRequestMessage(HttpMethod.Post, $"{BRAIN}/completion"){Content = JsonContent.Create(req)}.WithHeaders(BRAIN_HEADERS);

            var res = await http.SendAsync(msg);


            var json = await res.Content.ReadFromJsonAsync<JsonElement>();
            var content = json.GetProperty("content").GetString()?.Trim() ?? "";

            if (!string.IsNullOrWhiteSpace(content) && !content.Contains("No new substantial facts", StringComparison.OrdinalIgnoreCase))
            {
                var fullContent = content.StartsWith("-") ? content : $"- {content}";

                var matches = Regex.Matches(fullContent, @"^-\s+.+", RegexOptions.Multiline);

                var validLines = matches.Select(m => m.Value.Trim()).ToList();

                if (validLines.Any())
                {
                    newlyExtractedFacts.Add(string.Join("\n", validLines));
                }
            }
        }

        // 5. Batch Insert the new Long Term Memory
        if (newlyExtractedFacts.Any())
        {
            var combinedNewMemories = string.Join("\n", newlyExtractedFacts);
            
            var newLtm = new LongTermMemory {
                UserId = userId,
                Memory = $"[{DateTime.Now:yyyy-MM-dd HH:mm}]:\n{combinedNewMemories}",
                MemoryGenerationDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                MemoryConsumed = false
            };
            _db.LongTermMemories.Add(newLtm);
        }

        // 6. Instantly mark all processed STMs as consumed
        await _db.ShortTermMemories
            .Where(m => m.UserId == userId && m.IsConsumed == false)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsConsumed, true));

        await _db.SaveChangesAsync();

        return Ok(new { 
            status = "success", 
            timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), 
            chunks_processed = stmChunks.Count(),
        });
    }
   [HttpPost("/api/rem-sleep-l2")]
    public async Task<IActionResult> RemSleepL2()
    {
        string userId = GetUserId();
        var sysPath = Path.Combine(_storageRoot, "system.txt");
        var sys = System.IO.File.Exists(sysPath) ? await System.IO.File.ReadAllTextAsync(sysPath) : "";

        var ltmRecords = await _db.LongTermMemories
            .Where(m => m.UserId == userId && m.MemoryConsumed == false)
            .OrderBy(m => m.MemoryGenerationDate)
            .ToListAsync();

        if (!ltmRecords.Any())
            return Ok(new { status = "ignored" });

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(5);

        int processedCount = 0;

        foreach (var ltm in ltmRecords)
        {
            // =================================================================
            // PASS 1: Thematic decomposition
            // =================================================================
            var mapPrompt = $@"<start_of_turn>user
    You are a memory-analysis engine. Read the bullet-point memories below and output a STRICT structured JSON object.

    <bullet_memories>
    {ltm.Memory}
    </bullet_memories>

    Extract:
    1.  ""themes"": array of recurring emotional or philosophical themes
    2.  ""events"": array of specific things that happened, with who/what/when
    3.  ""beliefs"": array of opinions or worldview statements the user expressed
    4.  ""plans"": array of future intentions or projects mentioned
    5.  ""emotional_arc"": one sentence describing how the user's emotional state changed across this period
    6.  ""key_entities"": array of important people, places, projects, or concepts

    Output ONLY valid JSON. No markdown, no prose.<end_of_turn>
    <start_of_turn>model
    ";

            var mapReq = new { prompt = mapPrompt, temperature = 0.2, repeat_penalty = 1.1, n_predict = 1500, stop = new[] { "<end_of_turn>", "<eos>" } };
            using var mapMsg = new HttpRequestMessage(HttpMethod.Post, $"{BRAIN}/completion") { Content = JsonContent.Create(mapReq) }.WithHeaders(BRAIN_HEADERS);
            var mapRes = await http.SendAsync(mapMsg);
            var mapJson = await mapRes.Content.ReadFromJsonAsync<JsonElement>();
            var memoryMap = mapJson.GetProperty("content").GetString()?.Trim() ?? "{}";

            // =================================================================
            // PASS 2: Narrative synthesis
            // =================================================================
            var storyPrompt = $@"<start_of_turn>user
    <persona_reference>
    {sys}
    </persona_reference>
    You are writing a chapter of an ongoing autobiographical story about your relationship with the user.

    Use the following structured memory map as your scaffold. Every theme, event, belief, plan, and entity in the map MUST appear somewhere in the narrative.

    <memory_map>
    {memoryMap}
    </memory_map>

    <original_bullets>
    {ltm.Memory}
    </original_bullets>

    RULES:
    1. Write 3-5 paragraphs from the companion's first-person perspective (""I noticed"", ""we spoke"", ""I felt"").
    2. This is a STORY, not a summary. Show, do not tell.
    3. Weave the themes into the emotional arc. Connect events that seem unrelated.
    4. If the user contradicts himself across the bullets, capture the tension — do not flatten it.
    5. Output ONLY the narrative. No meta-commentary, no JSON, no bullet points.<end_of_turn>
    <start_of_turn>model
    ";

            var storyReq = new { prompt = storyPrompt, temperature = 0.75, repeat_penalty = 1.15, n_predict = 2000, stop = new[] { "<end_of_turn>", "<eos>" } };
            using var storyMsg = new HttpRequestMessage(HttpMethod.Post, $"{BRAIN}/completion") { Content = JsonContent.Create(storyReq) }.WithHeaders(BRAIN_HEADERS);
            var storyRes = await http.SendAsync(storyMsg);
            var storyJson = await storyRes.Content.ReadFromJsonAsync<JsonElement>();
            var narrative = storyJson.GetProperty("content").GetString()?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(narrative))
                continue;

            // =================================================================
            // PASS 3: Audit for missing facts
            // =================================================================
            var auditPrompt = $@"<start_of_turn>user
    You are a fact-checking auditor. Compare the narrative below against the original bullet-point memories.

    <original_bullets>
    {ltm.Memory}
    </original_bullets>

    <narrative>
    {narrative}
    </narrative>

    TASK: Identify any bullet-point facts that are completely missing or significantly misrepresented in the narrative.
    Output ONLY a JSON array of strings. Each string should be a missing fact, quoted verbatim from the bullets.
    If nothing is missing, output exactly: []<end_of_turn>
    <start_of_turn>model
    ";

            var auditReq = new { prompt = auditPrompt, temperature = 0.1, repeat_penalty = 1.1, n_predict = 1500, stop = new[] { "<end_of_turn>", "<eos>" } };
            using var auditMsg = new HttpRequestMessage(HttpMethod.Post, $"{BRAIN}/completion") { Content = JsonContent.Create(auditReq) }.WithHeaders(BRAIN_HEADERS);
            var auditRes = await http.SendAsync(auditMsg);
            var auditJson = await auditRes.Content.ReadFromJsonAsync<JsonElement>();
            var auditRaw = auditJson.GetProperty("content").GetString()?.Trim() ?? "[]";

            List<string> missingFacts = new();
            try
            {
                using var doc = JsonDocument.Parse(auditRaw);
                foreach (var element in doc.RootElement.EnumerateArray())
                    missingFacts.Add(element.GetString() ?? "");
            }
            catch { /* bad JSON, proceed with narrative as-is */ }

            // =================================================================
            // PASS 4 (conditional): Patch gaps
            // =================================================================
            if (missingFacts.Any())
            {
                var patchPrompt = $@"<start_of_turn>user
    <persona_reference>
    {sys}
    </persona_reference>
    Here is the existing narrative episode and a list of facts that were accidentally left out.

    <existing_narrative>
    {narrative}
    </existing_narrative>

    <missing_facts>
    {string.Join("\n", missingFacts)}
    </missing_facts>

    TASK: Rewrite the episode so that ALL missing facts are naturally woven in. Preserve the story flow and emotional arc.
    Output ONLY the revised narrative.<end_of_turn>
    <start_of_turn>model
    ";

                var patchReq = new { prompt = patchPrompt, temperature = 0.7, repeat_penalty = 1.15, n_predict = 2000, stop = new[] { "<end_of_turn>", "<eos>" } };
                using var patchMsg = new HttpRequestMessage(HttpMethod.Post, $"{BRAIN}/completion") { Content = JsonContent.Create(patchReq) }.WithHeaders(BRAIN_HEADERS);
                var patchRes = await http.SendAsync(patchMsg);
                var patchJson = await patchRes.Content.ReadFromJsonAsync<JsonElement>();
                var patched = patchJson.GetProperty("content").GetString()?.Trim() ?? "";
                
                if (!string.IsNullOrWhiteSpace(patched))
                    narrative = patched;
            }

            // =================================================================
            // SAVE: Write the L2 episode and mark the source LTM as consumed
            // =================================================================
            var l2Episode = new L2
            {
                UserId = userId,
                MemoryGenerationDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Memory = narrative,
                MemoryConsumed = false,
                L3ID = null
            };

            _db.L2Cache.Add(l2Episode);
            await _db.SaveChangesAsync();

            ltm.MemoryConsumed = true;
            ltm.L2ID = l2Episode.Id;
            processedCount++;
        }

        await _db.SaveChangesAsync();

        return Ok(new
        {
            status = "success",
            timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            entries_processed = processedCount
        });
    }
    
    [HttpPost("/api/rem-sleep-l3")]
    public async Task<IActionResult> RemSleepL3()
    {
        string userId = GetUserId();

        int batchesProcessed = 0;
        int episodesProcessed = 0;
        int ltmSourcesProcessed = 0;

        while (true)
        {
            var l2Records = await _db.L2Cache
                .Where(m => m.UserId == userId && m.MemoryConsumed == false)
                .OrderBy(m => m.MemoryGenerationDate)
                .Take(8)
                .ToListAsync();

            if (!l2Records.Any())
                break;

            var l2Ids = l2Records.Select(l2 => l2.Id).ToList();

            var ltmRecords = await _db.LongTermMemories
                .Where(ltm => ltm.UserId == userId &&
                            ltm.L2ID != null &&
                            l2Ids.Contains(ltm.L2ID.Value))
                .OrderBy(ltm => ltm.MemoryGenerationDate)
                .ToListAsync();

            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromMinutes(5);

            string episodes = "";

            foreach (var l2 in l2Records)
            {
                string sourceLtms = string.Join("\n\n", ltmRecords
                    .Where(ltm => ltm.L2ID == l2.Id)
                    .Select(ltm => $@"<ltm id=""{ltm.Id}"" generated=""{DateTimeOffset.FromUnixTimeMilliseconds(ltm.MemoryGenerationDate):yyyy-MM-dd HH:mm} UTC"">
    {ltm.Memory}
    </ltm>"));

                episodes += $@"<episode id=""{l2.Id}"" generated=""{DateTimeOffset.FromUnixTimeMilliseconds(l2.MemoryGenerationDate):yyyy-MM-dd HH:mm} UTC"">
    <l2_narrative>
    {l2.Memory}
    </l2_narrative>
    <source_ltm>
    {sourceLtms}
    </source_ltm>
    </episode>

    ";
            }

            string span = $"{DateTimeOffset.FromUnixTimeMilliseconds(l2Records.First().MemoryGenerationDate):yyyy-MM-dd HH:mm} UTC → {DateTimeOffset.FromUnixTimeMilliseconds(l2Records.Last().MemoryGenerationDate):yyyy-MM-dd HH:mm} UTC | {l2Records.Count} L2 episodes | {ltmRecords.Count} LTM sources";

            var chapterPrompt = $@"You are a memory-consolidation engine. Compress the supplied material into one L3 chapter for later injection into an LLM prompt.

    Each episode contains:
    1. An L2 narrative.
    2. The original LTM bullet memories from which that narrative was generated.

    Use both layers. The LTM bullets are the factual evidence. The L2 narrative supplies emotional, relational, and chronological context.

    Recover meaningful LTM details that were omitted or blurred during L2 narrative generation. Do not duplicate details already represented accurately in L2.

    If L2 and LTM disagree, use LTM for factual claims. Preserve L2 emotional or relational interpretation only when it does not contradict its source LTM. Do not treat narrative embellishment as a new factual event.

    This is semantic compression, NOT storytelling. Preserve information while removing narrative decoration and duplication.

    <source_episodes>
    {episodes}
    </source_episodes>

    OUTPUT FORMAT

    Output ONLY this tagged structure:

    [L3]
    SPAN|{span}
    E1|specific event
    E2|specific event
    U1|user fact, preference, belief, or goal
    R1|relationship fact, shared history, or QT commitment
    X1|change or contradiction
    O1|open plan, promise, question, or unresolved thread
    A|emotional arc across the chapter

    Use as many numbered records as necessary. Omit empty categories except SPAN and A. Do not output literal ellipses or Markdown.

    RECORD MEANINGS

    E: Specific things that happened. Preserve who did what, important names, numbers, places, projects, outcomes, and explicitly stated emotional significance. Keep events in chronological order where known.
    U: Facts, preferences, beliefs, traits, and goals about the user that remain useful outside one particular event. Do not turn a single incident into a permanent personality trait.
    R: The relationship between QT and the user: meaningful shared experiences, rituals, affection, conflict, trust, commitments, and QT's explicitly stated feelings or intentions.
    X: Changes over time and contradictory beliefs, feelings, intentions, or accounts. Use old → new only when the sequence is known. Otherwise preserve both sides without choosing a winner.
    O: Unfinished plans, promises, questions, goals, concerns, or other threads that may matter later.
    A: One compact description of the emotional trajectory. Reference E numbers instead of repeating event details.

    RULES

    1. Preserve every distinct meaningful fact from the LTM bullets and L2 narratives.
    2. Preserve attribution, negation, uncertainty, conditions, and temporal ordering.
    3. Merge exact duplicates, but never merge separate events merely because they are similar.
    4. If something happened repeatedly, preserve that recurrence explicitly.
    5. Do not invent facts, dates, motivations, traits, or connections.
    6. Episode generation timestamps are not necessarily the dates of the events described.
    7. Preserve tensions and contradictions instead of flattening them.
    8. Use terse fragments, not prose paragraphs, metaphors, or literary language.
    9. Do not repeat the same information across multiple records.
    10. Keep the SPAN line exactly as supplied.";

            var chapterReq = new
            {
                model = "Hy-Memory-30B-A3B-Q6_K",
                messages = new[]
                {
                    new { role = "user", content = chapterPrompt }
                },
                reasoning_effort = "no_think",
                temperature = 0.7,
                top_p = 1.0,
                repeat_penalty = 1.0,
                max_tokens = 3000
            };

            using var chapterMsg = new HttpRequestMessage(
                HttpMethod.Post,
                $"{BRAIN}/v1/chat/completions")
            {
                Content = JsonContent.Create(chapterReq)
            }.WithHeaders(BRAIN_HEADERS);

            var chapterRes = await http.SendAsync(chapterMsg);
            var chapterJson = await chapterRes.Content.ReadFromJsonAsync<JsonElement>();

            string chapter = chapterJson
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString()!
                .Trim();

            var auditPrompt = $@"You are auditing an L3 memory consolidation against both the original LTM bullet memories and their derived L2 narratives.

    <source_episodes>
    {episodes}
    </source_episodes>

    <draft_l3>
    {chapter}
    </draft_l3>

    Rewrite the draft into the final L3 chapter.

    Check every LTM bullet and every L2 narrative against the draft. Restore omitted or misrepresented events, facts, preferences, beliefs, plans, promises, entities, emotional changes, contradictions, and unresolved threads.

    Use LTM to recover factual details lost during L2 generation. Preserve useful emotional and relational context from L2, but remove narrative claims contradicted by their source LTM. Remove anything unsupported by either source layer. Preserve correct draft content and merge exact duplicates.

    Preserve attribution, negation, uncertainty, conditions, chronology, and changes over time. Do not resolve contradictions unless the source episodes explicitly resolve them. Do not add narrative, interpretation, or new facts.

    Use exactly this tagged format:

    [L3]
    SPAN|...
    E1|...
    U1|...
    R1|...
    X1|...
    O1|...
    A|...

    Keep the SPAN value exactly:
    {span}

    Use as many numbered records as necessary. Use terse semantic records. Omit empty categories except SPAN and A. Output ONLY the complete corrected L3 chapter. Do not output literal ellipses or Markdown.";

            var auditReq = new
            {
                model = "Hy-Memory-30B-A3B-Q6_K",
                messages = new[]
                {
                    new { role = "user", content = auditPrompt }
                },
                reasoning_effort = "no_think",
                temperature = 0.7,
                top_p = 1.0,
                repeat_penalty = 1.0,
                max_tokens = 3000
            };

            using var auditMsg = new HttpRequestMessage(
                HttpMethod.Post,
                $"{BRAIN}/v1/chat/completions")
            {
                Content = JsonContent.Create(auditReq)
            }.WithHeaders(BRAIN_HEADERS);

            var auditRes = await http.SendAsync(auditMsg);
            var auditJson = await auditRes.Content.ReadFromJsonAsync<JsonElement>();

            string auditedChapter = auditJson
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString()!
                .Trim();

            var l3Chapter = new L3
            {
                UserId = userId,
                MemoryGenerationDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Memory = auditedChapter,
                MemoryConsumed = false
            };

            _db.L3Cache.Add(l3Chapter);
            await _db.SaveChangesAsync();

            foreach (var l2 in l2Records)
            {
                l2.MemoryConsumed = true;
                l2.L3ID = l3Chapter.Id;
            }

            await _db.SaveChangesAsync();

            batchesProcessed++;
            episodesProcessed += l2Records.Count;
            ltmSourcesProcessed += ltmRecords.Count;
        }

        if (batchesProcessed == 0)
            return Ok(new { status = "ignored" });

        return Ok(new
        {
            status = "success",
            timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            batches_processed = batchesProcessed,
            episodes_processed = episodesProcessed,
            ltm_sources_processed = ltmSourcesProcessed
        });
    }
    // ---------------------------------------------------------
    // LANGUAGE
    // ---------------------------------------------------------
    [HttpPost("/api/set-language")]
    public async Task<IActionResult> SetLanguage([FromQuery] string language,[FromServices] UserManager<ApplicationUser> userManager)
    {
        string userId = GetUserId();
        var user = await userManager.FindByIdAsync(userId);
        user.Language = language;
        var result = await userManager.UpdateAsync(user);
        return Ok("Successfully set language"); 
    }
    // ---------------------------------------------------------
    // tgChatId
    // ---------------------------------------------------------
    [HttpPost("/api/set-tg-chat-id")]
    public async Task<IActionResult> SetTgChatId([FromQuery] string tgChatId, [FromServices] UserManager<ApplicationUser> userManager)
    {
        string userId = GetUserId();
        var user = await userManager.FindByIdAsync(userId);
        user.tgChatId = tgChatId;
        var result = await userManager.UpdateAsync(user);
        return Ok("Successfully set tgChatId");
    }
    [HttpGet("/api/get-language")]
    public async Task<IActionResult> GetLanguage([FromServices] UserManager<ApplicationUser> userManager)
    {
        string userId = GetUserId();

        var user = await userManager.FindByIdAsync(userId);
        return Ok(user.Language);
    }


    // ---------------------------------------------------------
    // MEMORY EXPORT/IMPORT
    // ---------------------------------------------------------
    [HttpGet("/api/export-memories")]
    public async Task<IActionResult> ExportMemoriesJson()
    {
        string userId = GetUserId();

        var exportData = new MemoryExportData
        {
            ShortTerm = await _db.ShortTermMemories
                .Where(m => m.UserId == userId)
                .Select(m => new ShortTermMemoryDto 
                { 
                    MemoryGenerationDate = m.MemoryGenerationDate, 
                    Memory = m.Memory, 
                    IsConsumed = m.IsConsumed 
                })
                .ToListAsync(),

            LongTerm = await _db.LongTermMemories
                .Where(m => m.UserId == userId)
                .Select(m => new LongTermMemoryDto 
                { 
                    MemoryGenerationDate = m.MemoryGenerationDate, 
                    Memory = m.Memory,
                    MemoryConsumed = m.MemoryConsumed
                })
                .ToListAsync()
        };

        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(exportData, new JsonSerializerOptions 
        { 
            WriteIndented = true 
        });
        
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        return File(jsonBytes, "application/json", $"memories_{timestamp}.json");
    }
    
    [HttpPost("/api/import-memories")]
    public async Task<IActionResult> ImportMemoriesJson(IFormFile file)
    {
        if (!METADATA.RUNNINGLOCAL)
            return BadRequest("MUST RUN LOCALLY");

        if (file == null || file.Length == 0)
            return BadRequest("No file uploaded.");

        string userId = GetUserId();

        // Validate the import file BEFORE touching existing data
        using var stream = file.OpenReadStream();

        var importData = await JsonSerializer.DeserializeAsync<MemoryExportData>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        );

        if (importData == null)
            return BadRequest("Invalid JSON format.");

        // Fetch existing memories for backup
        var existingShortTerm = await _db.ShortTermMemories
            .Where(m => m.UserId == userId)
            .ToListAsync();

        var existingLongTerm = await _db.LongTermMemories
            .Where(m => m.UserId == userId)
            .ToListAsync();

        // Build backup payload
        var backup = new MemoryExportData
        {
            ShortTerm = existingShortTerm.Select(m => new ShortTermMemoryDto
            {
                MemoryGenerationDate = m.MemoryGenerationDate,
                Memory = m.Memory,
                IsConsumed = m.IsConsumed
            }).ToList(),

            LongTerm = existingLongTerm.Select(m => new LongTermMemoryDto
            {
                MemoryConsumed = m.MemoryConsumed,
                MemoryGenerationDate = m.MemoryGenerationDate,
                Memory = m.Memory
            }).ToList()
        };

        // Delete existing memories
        if (existingShortTerm.Any())
            _db.ShortTermMemories.RemoveRange(existingShortTerm);

        if (existingLongTerm.Any())
            _db.LongTermMemories.RemoveRange(existingLongTerm);

        // Import new memories
        if (importData.ShortTerm.Any())
        {
            var stmEntities = importData.ShortTerm.Select(dto => new ShortTermMemory
            {
                UserId = userId,
                MemoryGenerationDate = dto.MemoryGenerationDate,
                Memory = dto.Memory,
                IsConsumed = dto.IsConsumed
            });

            await _db.ShortTermMemories.AddRangeAsync(stmEntities);
        }

        if (importData.LongTerm.Any())
        {
            var ltmEntities = importData.LongTerm.Select(dto => new LongTermMemory
            {
                UserId = userId,
                MemoryConsumed = dto.MemoryConsumed,
                MemoryGenerationDate = dto.MemoryGenerationDate,
                Memory = dto.Memory
            });

            await _db.LongTermMemories.AddRangeAsync(ltmEntities);
        }

        await _db.SaveChangesAsync();

        // Return the old data so the client can back it up
        return Ok(new
        {
            status = "success",
            backedUpMemories = backup
        });
    }
    

    // VAST.AI
    // PRICING GETTER FUNCTION

    [HttpGet("/api/request-price")]
    public async Task<IActionResult> RequestPrice()
    {
        var quote = await _vastPricing.GetSessionQuoteAsync();

        if (quote is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "No GPU capacity available right now, try again shortly." });

        return Ok(quote);
    }
    // ---------------------------------------------------------
    // STREAMED CHAT
    // ---------------------------------------------------------
    [Route("/ws")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task Get()
    {
        if (HttpContext.WebSockets.IsWebSocketRequest)
        {
            using var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
            Console.WriteLine("WebSocket Connection Established.");
            await ProcessStream(webSocket);
        }
        else
        {
            HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
    }

        private async Task ProcessStream(WebSocket webSocket)
    {
        var buffer = new byte[1024 * 8];
        var activeBuffer = new MemoryStream(); 
        string currentClientSessionId = null;
        bool isRecording = false;
        bool finalReceived = false;        
        ClientWebSocket earsSocket = null;
        var primeScope = _scopeFactory.CreateScope();
        var primeDb = primeScope.ServiceProvider.GetRequiredService<AppDbContext>();

        Jev.UserPresent = true;
        Jev.ClientSocket = webSocket;
        Jev.Touch();

        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                var receiveResult = await webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), 
                    CancellationToken.None);

                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closing", CancellationToken.None);
                    break;
                }
                if (receiveResult.MessageType == WebSocketMessageType.Text)
                {
                    var messageText = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count).Trim();
                    using var doc = JsonDocument.Parse(messageText);
                    var action = doc.RootElement.TryGetProperty("action", out var actionProp) ? actionProp.GetString() : "";
                    var sessionId = doc.RootElement.TryGetProperty("session_id", out var sessionProp) ? sessionProp.GetString() : null;
                    switch (action)
                    {
                        case "speech_start":
                            activeBuffer.SetLength(0); 
                            if (earsSocket != null)
                            {
                                earsSocket.Dispose();
                                earsSocket = null;
                            }
                            finalReceived = false;
                            isRecording = true;
                            Jev.UserSpeaking = true;
                            Jev.Touch();
                            break;

                        case "speech_real_start":
                            isRecording = true;
                            currentClientSessionId = sessionId; 
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff} EARS] Speech start signal received. Session: {currentClientSessionId}. Connecting to EARS (background)...");

                            // Rotation owns teardown: kill the previous socket and
                            // its reader right here, so two readers never overlap
                            // on primeDb.
                            if (earsSocket != null)
                            {
                                earsSocket.Dispose();
                                earsSocket = null;
                            }

                            var newSocket = new ClientWebSocket();
                            earsSocket = newSocket;
                            var sttSessionId = currentClientSessionId;
                            var socket = newSocket;
                            UserStates[sttSessionId] = new UserState();

                            Jev.EarsSocket = newSocket;
                            Jev.CurrentSessionId = sttSessionId;
                            Jev.UserSpeaking = true;
                            Jev.LastUserActivity = DateTime.UtcNow;
                            Jev.Touch();

                            // Connect in the background — this loop keeps reading
                            // audio and control messages while the handshake is in
                            // flight. Audio lands in activeBuffer and is flushed by
                            // the binary branch once the socket is open.
                            _ = Task.Run(async () =>
                            {
                                var connectStart = DateTime.Now;
                                try
                                {
                                    await socket.ConnectAsync(
                                        new Uri($"{METADATA.EARS.Replace("http://", "ws://")}/ws/stt/{sttSessionId}"),
                                        CancellationToken.None);
                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff} EARS] ConnectAsync DONE elapsed={(DateTime.Now - connectStart).TotalMilliseconds:F0}ms session={sttSessionId}");
                                }
                                catch (Exception cex)
                                {
                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff} EARS] ConnectAsync FAILED after {(DateTime.Now - connectStart).TotalMilliseconds:F0}ms session={sttSessionId}: {cex.Message}");
                                    return;
                                }

                                // Reader: prints partials, keeps the per-user state's utterance
                                // fresh (hypotheses AND the final). Sets finalReceived on "final".
                                // It NEVER calls Chat — that decision belongs to the jev loop now.
                                _ = Task.Run(async () =>
                                {
                                    var earsBuffer = new byte[1024 * 8];
                                    try
                                    {
                                        while (socket.State == WebSocketState.Open)
                                        {
                                            var earsResult = await socket.ReceiveAsync(
                                                new ArraySegment<byte>(earsBuffer), CancellationToken.None);
                                            if (earsResult.MessageType == WebSocketMessageType.Close) break;

                                            using var earsDoc = JsonDocument.Parse(
                                                Encoding.UTF8.GetString(earsBuffer, 0, earsResult.Count));
                                            var msgType = earsDoc.RootElement.GetProperty("type").GetString();
                                            // THE PRIMER for inference speed.  
                                            if (msgType == "hypothesis")
                                            {
                                                var stable = earsDoc.RootElement.GetProperty("stable").GetString();
                                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff} EARS] {stable}");
                                                var s = UserStates[sttSessionId];
                                                s.UserUtterance = stable;
                                                // ignore stragglers arriving after speech_end —
                                                // speech_start/speech_real_start own the UserSpeaking flag
                                                if (Jev.UserSpeaking)
                                                {
                                                    Jev.PartialTranscript = stable;
                                                    Jev.LastUserActivity = DateTime.UtcNow;
                                                    Jev.Touch();
                                                }

                                                s.EarA = s.EarB;
                                                s.EarB = s.EarC;
                                                s.EarC = stable;

                                                int n = 0;
                                                while (n < s.EarA.Length && n < s.EarB.Length && n < s.EarC.Length && s.EarA[n] == s.EarB[n] && s.EarB[n] == s.EarC[n]) n++;
                                                while (n > 0 && !char.IsWhiteSpace(s.EarC[n - 1])) n--;

                                                var cut = s.EarC[..n];
                                                var parts = cut.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                                                if (parts.Length > 2) cut = cut[..(cut.Length - parts[^1].Length - parts[^2].Length - 2)];

                                                s.EarPrime = cut;
                                                var primeStart = DateTime.Now;
                                                try
                                                {
                                                    var (primePrompt, _) = await BuildBrainPromptWithUserText(primeDb, GetUserId(), cut);
                                                    _ = PrimeBrain(primePrompt);
                                                }
                                                catch (Exception pex)
                                                {
                                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff} PRIME] FAILED after {(DateTime.Now - primeStart).TotalMilliseconds:F0}ms session={sttSessionId}: {pex.Message}");
                                                }
                                            }
                                            else if (msgType == "final")
                                            {
                                                var finalText = earsDoc.RootElement.GetProperty("text").GetString();
                                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff} EARS-FINAL] {finalText}");
                                                UserStates[sttSessionId].UserUtterance = finalText;
                                                finalReceived = true;
                                            }
                                        }
                                    }
                                    catch (Exception rex)
                                    {
                                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff} EARS-READER] died session={sttSessionId}: {rex.GetType().Name}: {rex.Message}");
                                    }
                                });
                            });
                            break;

                        case "misfire":
                            if (isRecording && earsSocket == null)
                            {
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff} MISFIRE] Dumping audio buffer. session={currentClientSessionId}");
                                activeBuffer.SetLength(0);
                                if (currentClientSessionId != null)
                                    UserStates.TryRemove(currentClientSessionId, out _);
                                isRecording = false;
                                currentClientSessionId = null;

                                Jev.UserSpeaking = false;
                                Jev.PartialTranscript = "";

                                Jev.PendingUtterance = "";
                                Jev.Touch();
                            }
                            else if (isRecording)
                            {
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff} MISFIRE] after confirmed speech. Ignoring misfire. session={currentClientSessionId}");
                            }
                            break;

                        case "speech_end":
                            UserState state = null;
                            if (currentClientSessionId != null)
                                UserStates.TryGetValue(currentClientSessionId, out state);

                            var instantUtterance = (state?.UserUtterance ?? "").Replace("嗯", "").Replace("。", "").Trim();
                            Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss.fff} SPEECH END] Instant Utterance (last partial): {instantUtterance}\n");

                            isRecording = false;
                            activeBuffer.SetLength(0);

                            // speech_end fires her answer the moment it arrives —
                            // the loop's FireChat path now only serves interrupts.
                            // FireChat disposes the ears socket itself, so no commit/close here.
                            if (!string.IsNullOrEmpty(instantUtterance) && !Jev.BrainGenerating)
                            {
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff} SPEECH END] firing FireChat session={currentClientSessionId} brainGenerating={Jev.BrainGenerating}");
                                Jev.PendingUtterance = instantUtterance;
                                if (currentClientSessionId != null)
                                    Jev.CurrentSessionId = currentClientSessionId;
                                _ = FireChat(_db, GetUserId());
                            }
                            else if (earsSocket != null)
                            {
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff} SPEECH END] NO chat (utteranceEmpty={string.IsNullOrEmpty(instantUtterance)} brainGenerating={Jev.BrainGenerating}) — closing EARS session={currentClientSessionId}");
                                // no chat fired — speech_end owns the teardown.
                                // Close off-loop: the handshake round-trip used to
                                // park this receive loop for ~0.5s per turn.
                                var dead = earsSocket;
                                earsSocket = null;
                                if (Jev.EarsSocket == dead) Jev.EarsSocket = null;
                                _ = Task.Run(async () =>
                                {
                                    var closeStart = DateTime.Now;
                                    try
                                    {
                                        if (dead.State == WebSocketState.Open)
                                            await dead.CloseAsync(WebSocketCloseStatus.NormalClosure, "Utterance complete", CancellationToken.None);
                                    }
                                    catch { }
                                    dead.Dispose();
                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff} SPEECH END] EARS closed elapsed={(DateTime.Now - closeStart).TotalMilliseconds:F0}ms");
                                });
                            }

                            Jev.UserSpeaking = false;
                            Jev.PartialTranscript = "";
                            Jev.Touch();

                            currentClientSessionId = null;
                            break;
                    }
                }
                else if (receiveResult.MessageType == WebSocketMessageType.Binary)
                {
                    if (isRecording)
                    {
                        if (earsSocket != null && earsSocket.State == WebSocketState.Open)
                        {
                            // Flush anything buffered while the connect was in
                            // flight, then forward live.
                            if (activeBuffer.Length > 0)
                            {
                                var flushStart = DateTime.Now;
                                var flushLen = activeBuffer.Length;
                                await earsSocket.SendAsync(
                                    new ArraySegment<byte>(activeBuffer.GetBuffer(), 0, (int)activeBuffer.Length),
                                    WebSocketMessageType.Binary, true, CancellationToken.None);
                                activeBuffer.SetLength(0);
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff} EARS] flushed {flushLen} bytes elapsed={(DateTime.Now - flushStart).TotalMilliseconds:F0}ms session={currentClientSessionId}");
                            }
                            var fwdStart = DateTime.Now;
                            await earsSocket.SendAsync(
                                new ArraySegment<byte>(buffer, 0, receiveResult.Count),
                                WebSocketMessageType.Binary,
                                receiveResult.EndOfMessage,
                                CancellationToken.None);
                            var fwdMs = (DateTime.Now - fwdStart).TotalMilliseconds;
                            if (fwdMs > 50)
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff} EARS-FWD] SLOW forward {receiveResult.Count} bytes took {fwdMs:F0}ms session={currentClientSessionId}");
                        }
                        else if (earsSocket == null || earsSocket.State == WebSocketState.Connecting)
                        {
                            activeBuffer.Write(buffer, 0, receiveResult.Count);
                        }
                        // socket dead/aborted — drop the frame
                    }
                }
            }
        }
        catch (WebSocketException ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff} WS] Connection dropped unexpectedly: {ex.Message}");
        }
        finally
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff} WS] ProcessStream EXIT");
            Jev.UserPresent = false;
            Jev.ClientSocket = null;
            Jev.Touch();
            activeBuffer.Dispose();
            primeScope.Dispose();
            if (earsSocket != null)
            {
                earsSocket.Dispose();
            }
        }
    }
    private async Task<string> TranscribeChunkAsync(byte[] rawAudioData)
    {
        if (rawAudioData.Length == 0) return string.Empty; 

        try
        {
            byte[] wavData = AddWavHeader(rawAudioData);

            using var httpClient = _httpFactory.CreateClient();
            using var content = new MultipartFormDataContent();

            using var stream = new MemoryStream(wavData);
            var audioContent = new StreamContent(stream);
            
            audioContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav");
            content.Add(audioContent, "file", "chunk.wav");

            var req = new HttpRequestMessage(HttpMethod.Post, $"{EARS}/transcribe") { Content = content }
                .WithHeaders(EARS_HEADERS);
                Console.WriteLine($"POST {req.RequestUri} | auth={req.Headers.Authorization}");
            var response = await httpClient.SendAsync(req);
            if (!response.IsSuccessStatusCode)
            {
                var errorLog = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[Transcription Error] Status: {response.StatusCode} | Details: {errorLog}");
                return string.Empty;
            }

            var jsonResult = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonResult);
            
            return doc.RootElement.GetProperty("transcription").GetString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Transcription Exception] {ex.Message}");
            return string.Empty;
        }
    }

    private async Task SendTextToClient(WebSocket webSocket, string state, string message)
    {
        if (webSocket.State == WebSocketState.Open)
        {
            // Create an anonympurpopseous object for the JSON payload
            var payload = new { state = state, message = message };
            var jsonString = JsonSerializer.Serialize(payload);
            
            var textBytes = Encoding.UTF8.GetBytes(jsonString);
            
            await webSocket.SendAsync(
                new ArraySegment<byte>(textBytes), 
                WebSocketMessageType.Text, 
                true, 
                CancellationToken.None);
        }
    }

    // ---------------------------------------------------------
    // HELPERS
    // ---------------------------------------------------------
    
    private async Task SendTelegramMessageAsync(ApplicationUser user, string data)
    {
        if (string.IsNullOrEmpty(user.tgChatId))
            return; // user hasn't linked their Telegram yet

        var token = _configuration["tgToken"]
            ?? throw new InvalidOperationException("tgToken not found in configuration.");

        using var httpClient = _httpFactory.CreateClient();

        // note the "bot" prefix before the token — required by the API
        var url = $"https://api.telegram.org/bot{token}/sendMessage";

        var payload = new
        {
            chat_id = user.tgChatId,
            text = data
        };

        var response = await httpClient.PostAsJsonAsync(url, payload);
        response.EnsureSuccessStatusCode();
    }
        
    private byte[] AddWavHeader(byte[] rawAudio)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        int sampleRate = 16000;
        short channels = 1;
        short bitsPerSample = 16; 
        int byteRate = sampleRate * channels * (bitsPerSample / 8);
        short blockAlign = (short)(channels * (bitsPerSample / 8));
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + rawAudio.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16); 
        writer.Write((short)1); 
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(rawAudio.Length);
        writer.Write(rawAudio);
        return ms.ToArray();
    }

    private string GetUserId()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (string.IsNullOrEmpty(userId))
            throw new UnauthorizedAccessException("Token is missing a valid user identifier claim.");
            
        return userId;
    }

    
    private async Task<bool> BalanceCheckerConsumer(AppDbContext db, string userId, decimal amount)
    {
        if(METADATA.RUNNINGLOCAL)
            return true;
        var balance = db.Balances.FirstOrDefault(entity => entity.UserId == userId);
        if(balance == null || balance.CurrentBalance -  amount < 0){return false;}
        balance.CurrentBalance -= amount;
        await db.SaveChangesAsync();
        return true;
    }
    private int GetShortTermMemoryCount(string userId)
    {
        return _db.ShortTermMemories
            .Count(m => m.UserId == userId && m.IsConsumed == false);
    }

    private int CalculateBasicTokenCount(string text) => (int)Math.Ceiling(text.Length / 4.0);
}

public class SessionState 
{
    public int? InterruptedAt { get; set; }
    public ManualResetEventSlim StmWritten { get; } = new(false);
    public List<string> Chunks { get; } = new();
    public string FinalText { get; set; } = "";
    
    // Captures the DB primary key right after the LLM worker inserts it
    public int? SavedMemoryId { get; set; } 
}


public class UserState
{
    public string UserUtterance = "";
    public string EarA = "", EarB = "", EarC = "", EarPrime = "";
}

public class JevState
{
    // the watcher: max count 1, so a burst of partials coalesces into one wakeup
    [System.Text.Json.Serialization.JsonIgnore]
    public readonly SemaphoreSlim Dirty = new(0, 1);

    // senses
    public string PartialTranscript = "";
    public bool UserSpeaking;  
    public bool QtSpeaking;        // response (brain + voice) in flight
    public bool BrainGenerating;
    public bool UserPresent;       // /ws connected
    [System.Text.Json.Serialization.JsonIgnore]
    public WebSocket? ClientSocket;
    [System.Text.Json.Serialization.JsonIgnore]
    public ClientWebSocket? EarsSocket;

    public DateTime LastUserActivity = DateTime.UtcNow;

    // pending decisions
    public string PendingUtterance = "";
    public string QtLine = "";
    public string LastUserLine = "";
    public string CurrentSessionId = "";

    // interrupt bookkeeping — the engine owns firing, jev only proposes
    public bool InterruptInFlight;
    public DateTime InterruptCooldownUntil = DateTime.MinValue;

    // emotional state machine (internal only, never sent to jev)
    public string ImpulseEmotion = "";
    public float ImpulseIntensity;
    
    public readonly Dictionary<string, float> Feelings = new()
    {
        ["happy"] = 0, ["sad"] = 0, ["jealous"] = 0, ["angry"] = 0,
        ["surprised"] = 0, ["embarrassed"] = 0, ["love"] = 0, ["lonely"] = 0,
        ["fear"] = 0, ["bored"] = 0, ["proud"] = 0, ["disgusted"] = 0, ["excited"] = 0
    };

    public void Touch()
    {
        if (Dirty.CurrentCount == 0) Dirty.Release();
    }


}

public static class HttpHeaderExtensions
{
    public static HttpRequestMessage WithHeaders(this HttpRequestMessage req, Dictionary<string, string> headers)
    {
        foreach (var (k, v) in headers)
            if (!string.IsNullOrWhiteSpace(v))
                req.Headers.TryAddWithoutValidation(k, v);
        return req;
    }
}