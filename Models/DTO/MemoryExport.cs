using System.Text.Json;

public class MemoryExportData
{
    public List<ShortTermMemoryDto> ShortTerm { get; set; } = new();
    public List<LongTermMemoryDto> LongTerm { get; set; } = new();
}

public class ShortTermMemoryDto
{
    public long MemoryGenerationDate { get; set; }
    public required string Memory { get; set; }
    public bool IsConsumed { get; set; }
}

public class LongTermMemoryDto
{
    public long MemoryGenerationDate { get; set; }
    public required string Memory { get; set; }
}