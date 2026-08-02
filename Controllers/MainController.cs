using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Diagnostics;
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
using System.IO.Compression;

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
    
    static readonly ConcurrentDictionary<string, SessionState> _sessions = new();
    
    readonly IHttpClientFactory _httpFactory;
    readonly IServiceScopeFactory _scopeFactory;
    readonly AppDbContext _db;
    readonly string _storageRoot;

    public MainController(IHttpClientFactory httpFactory, IServiceScopeFactory scopeFactory, AppDbContext db)
    {
        _httpFactory = httpFactory;
        _scopeFactory = scopeFactory;
        _db = db;
        _storageRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Storage");
        Directory.CreateDirectory(_storageRoot);
    }
    
    // ---------------------------------------------------------
    // CHAT ENDPOINT
    // ---------------------------------------------------------
    [HttpPost("/api/chat")]
    public async Task<IActionResult> Chat([FromBody] JsonElement data)
    {
        string userId = GetUserId();
        decimal price = 0.05m;
        if (!await BalanceCheckerConsumer(userId, price)) return Unauthorized();
          
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
        string sysContent = System.IO.File.Exists(globalSysPath) ? await System.IO.File.ReadAllTextAsync(globalSysPath) : "";

        var ltmRecords = await _db.LongTermMemories
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.MemoryGenerationDate)
            .Select(m => m.Memory)
            .ToListAsync();

        var stmRecords = await _db.ShortTermMemories
            .Where(m => m.UserId == userId && m.IsConsumed == false)
            .OrderBy(m => m.MemoryGenerationDate)
            .Select(m => m.Memory)
            .ToListAsync();

        string ltmString = string.Join("\n", ltmRecords);
        string stmString = string.Join("\n", stmRecords);

        string prompt = $"<bos><start_of_turn>user\n{sysContent}\n\n### LONG TERM MEMORY ###\n{ltmString}\n\n### RECENT CONVERSATION ###\n{stmString}\n\nUser: {userText}<end_of_turn>\n<start_of_turn>model\n";
        

        if (skipAudio)
        {
            string reply = await LlmTextOnly(userId, prompt, userText);
            return Ok(new { status = "success", text = reply });
        }

        _ = Task.Run(() => LlmWorkerThread(userId, sessionId, prompt, userText, state));
        return Ok(new { status = "success", session_id = sessionId });
    }
    private async Task LlmWorkerThread(string userId, string sessionId, string prompt, string userText, SessionState state)
    {
        string targetLanguage = "en"; 
        var user = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId);
            
        targetLanguage = user.Language;
        

        var audioQueue = Channel.CreateUnbounded<(int index, string text)>();
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
                    try {
                        // 1. Create the request message
                        var request = new HttpRequestMessage(HttpMethod.Post, $"{MOUTH}/v1/audio/generate/{sessionId}/{i}").WithHeaders(MOUTH_HEADERS);
                        
                        // 2. Add the static API key header
                        request.Headers.Add("X-Api-Key", "change-this-to-a-secure-random-string-fuckasspiss");
                        
                        // 3. Serialize and attach the payload
                        var jsonContent = JsonSerializer.Serialize(payload,jsonOptions);
                        request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                        
                        // 4. Fire the request
                        var res = await http.SendAsync(request);
                        
                        res.EnsureSuccessStatusCode();
                        
                        // Break out of the retry loop on success
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
            using var msg = new HttpRequestMessage(HttpMethod.Post, $"{BRAIN}/completion") { Content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json") }.WithHeaders(METADATA.BRAIN_HEADERS);;
            using var response = await http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead);
            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

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
                    cleanText = Regex.Replace(cleanText, @"\s+", " ");

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
                    Memory = $"User: {userText}\nQT: {state.FinalText}\n",
                    MemoryGenerationDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    IsConsumed = false
                };

                backgroundDb.ShortTermMemories.Add(newMemory);
                await backgroundDb.SaveChangesAsync();
                
                state.SavedMemoryId = newMemory.Id; 
            }

            state.StmWritten.Set();
            await audioTask;
        }
    }

    private async Task<string> LlmTextOnly(string userId, string prompt, string userText)
    {
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(5);

        string rawResponse = "";
        var req = new { prompt, n_predict = 2400, temperature = 0.6, repeat_penalty = 1.15, stop = new[] { "<end_of_turn>", "<eos>", "User:" }, stream = true };

        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{BRAIN}/completion") { Content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json") }.WithHeaders(METADATA.BRAIN_HEADERS);
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
        var newMemory = new ShortTermMemory
        {
            UserId = userId,
            Memory = $"User: {userText}\nQT: {cleanText}\n",
            MemoryGenerationDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            IsConsumed = false
        };

        _db.ShortTermMemories.Add(newMemory);
        await _db.SaveChangesAsync();

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
        if (!await BalanceCheckerConsumer(userId, 0.001m)) 
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

            using var msg = new HttpRequestMessage(HttpMethod.Post, $"{METADATA.EARS}/transcribe")
            {
                Content = content   
            }.WithHeaders(METADATA.EARS_HEADERS);

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
            .Where(m => m.UserId == userId)
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

        var expJson = JsonDocument.Parse(await expResponse.Content.ReadAsStringAsync());
        
        decimal xmrReceived = 0;
        if (expJson.RootElement.TryGetProperty("data", out var dataNode) && 
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
    // INTERRUPT 
    // ---------------------------------------------------------
    [HttpPost("/api/interrupt")]
    public async Task<IActionResult> Interrupt([FromBody] JsonElement data)
    {
        string userId = GetUserId(); 

        var sessionId = data.TryGetProperty("session_id", out var s) ? s.GetString() : null;
        if (string.IsNullOrEmpty(sessionId) || !data.TryGetProperty("chunkID", out var c)) 
            return BadRequest(new { status = "error", message = "Invalid data payload" });

        int chunkId = c.GetInt32();

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

                if (cutoff > 0 && cutoff < state.FinalText.Length) {
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
    [HttpPost("/api/rem-sleep")]
    public async Task<IActionResult> RemSleep()
    {
        string userId = GetUserId();
        var sysPath = Path.Combine(_storageRoot, "system.txt");
        var sys = System.IO.File.Exists(sysPath) ? await System.IO.File.ReadAllTextAsync(sysPath) : "";

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

            var prompt = $@"<bos><start_of_turn>user
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
                <start_of_turn>model
                ### NEW MEMORIES ###
                -";

            var req = new { prompt, temperature = 0.1, repeat_penalty = 1.15, n_predict = 400, stop = new[] { "<end_of_turn>", "<eos>" } };
            using var msg = new HttpRequestMessage(HttpMethod.Post, $"{METADATA.BRAIN}/completion"){Content = JsonContent.Create(req)}.WithHeaders(METADATA.BRAIN_HEADERS);

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
                MemoryGenerationDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
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
                    Memory = m.Memory 
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
        var currentUtterance = new StringBuilder();
        string currentClientSessionId = null;
        bool isRecording = false; 

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
                            currentUtterance.Clear();
                            isRecording = true;
                            currentClientSessionId = sessionId; 
                            Console.WriteLine($"Speech start signal received. Session: {currentClientSessionId}. Recording...");
                            break;

                        case "misfire":
                            if (isRecording) 
                            {
                                Console.WriteLine("Misfire signal received. Dumping audio buffer.");
                                activeBuffer.SetLength(0); 
                                currentUtterance.Clear();
                                isRecording = false;       
                            }
                            break;

                        case "speech_chunk":
                            if (isRecording && activeBuffer.Length > 0)
                            {
                                var text = await TranscribeChunkAsync(activeBuffer.ToArray());
                                activeBuffer.SetLength(0); 
                                
                                if (!string.IsNullOrWhiteSpace(text))
                                {
                                    currentUtterance.Append(text).Append(' ');
                                    await SendTextToClient(webSocket, "text", text);
                                }
                            }
                            break;

                        case "speech_end":
                            if (isRecording && activeBuffer.Length > 0)
                            {
                                var text = await TranscribeChunkAsync(activeBuffer.ToArray());
                                activeBuffer.SetLength(0);

                                if (!string.IsNullOrWhiteSpace(text))
                                {
                                    currentUtterance.Append(text).Append(' ');
                                    await SendTextToClient(webSocket, "text", text);
                                }
                            }

                            var finalUtterance = currentUtterance.ToString().Replace("嗯", "").Replace("。", "").Trim();
                            Console.WriteLine($"\n[SPEECH END] Final Utterance: {finalUtterance}\n");

                            if (!string.IsNullOrEmpty(finalUtterance))
                            {
                                try
                                {
                                    // 1. Prepare the payload
                                    var postPayload = new { 
                                        text = finalUtterance,
                                        session_id = currentClientSessionId 
                                    };
                                    
                                    // 2. Convert it into a JsonElement (since your Chat method demands one)
                                    string jsonString = JsonSerializer.Serialize(postPayload);
                                    using var jsonDoc = JsonDocument.Parse(jsonString);
                                    JsonElement payloadElement = jsonDoc.RootElement.Clone();

                                    Console.WriteLine($"Calling Chat function directly for Session: {currentClientSessionId}");
                                    
                                    // 3. CALL THE DAMN FUNCTION directly!
                                    IActionResult result = await Chat(payloadElement);

                                    // 4. Check if it succeeded (Chat returns OkObjectResult on success)
                                    if (result is OkObjectResult okResult)
                                    {
                                        // Serialize the returned anonymous object to grab the session_id
                                        var responseJson = JsonSerializer.Serialize(okResult.Value);
                                        using var docChat = JsonDocument.Parse(responseJson);
                                        
                                        if (docChat.RootElement.TryGetProperty("session_id", out var sessionIdElement))
                                        {
                                            string returnSessionId = sessionIdElement.GetString() ?? "";
                                            Console.WriteLine($"[Chat Function Success] Session ID: {returnSessionId}");
                                            
                                            await SendTextToClient(webSocket, "session", returnSessionId);
                                        }
                                    }
                                    else
                                    {
                                        // It hit one of your Unauthorized() or BadRequest() returns
                                        Console.WriteLine($"[Chat Function Failed] Returned type: {result.GetType().Name}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[Direct Call Exception] {ex.Message}");
                                }
                            }

                            isRecording = false;
                            currentUtterance.Clear(); // Make sure to clear it for the next time they speak!
                            currentClientSessionId = null; // Reset it for the next utterance
                            break;
                    }
                }
                else if (receiveResult.MessageType == WebSocketMessageType.Binary)
                {
                    if (isRecording)
                    {
                        activeBuffer.Write(buffer, 0, receiveResult.Count);
                    }
                }
            }
        }
        catch (WebSocketException ex)
        {
            Console.WriteLine($"Connection dropped unexpectedly: {ex.Message}");
        }
        finally
        {
            activeBuffer.Dispose();
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

            var response = await httpClient.PostAsync($"{EARS}/transcribe", content);

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
            // Create an anonymous object for the JSON payload
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
        writer.Write(byteRate);
        writer.Write(blockAlign);
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

    
    private async Task<bool> BalanceCheckerConsumer(string userId, decimal amount)
    {
        if(METADATA.RUNNINGLOCAL)
            return true;
        var balance = _db.Balances.FirstOrDefault(entity => entity.UserId == userId);
        if(balance == null || balance.CurrentBalance -  amount < 0){return false;}
        balance.CurrentBalance -= amount;
        await _db.SaveChangesAsync();
        return true;
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

public static class HttpHeaderExtensions
{
    public static HttpRequestMessage WithHeaders(this HttpRequestMessage msg, Dictionary<string, string> headers)
    {
        foreach (var (k, v) in headers)
        {
            if (!msg.Headers.TryAddWithoutValidation(k, v))
                msg.Content?.Headers.TryAddWithoutValidation(k, v);
        }
        return msg;
    }
}
