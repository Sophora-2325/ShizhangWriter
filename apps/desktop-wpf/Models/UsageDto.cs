namespace NovelGm.Desktop.Models;

public sealed class UsageDto
{
    public long Input_Tokens { get; set; }
    public long Output_Tokens { get; set; }
    public long Cached_Tokens { get; set; }
    public decimal Cost_Usd { get; set; }
    public long Calls { get; set; }
    public long Unknown_Price_Calls { get; set; }
    public UsageCallDto? Last { get; set; }
    public long TotalTokens => Input_Tokens + Output_Tokens;
}

public sealed class UsageCallDto
{
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string Purpose { get; set; } = "";
    public decimal Cost_Usd { get; set; }
    public bool Price_Known { get; set; }
}
