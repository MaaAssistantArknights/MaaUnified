namespace MAAUnified.Application.Models.TaskParams;

public sealed class ReclamationTaskParamsDto
{
    public string Theme { get; set; } = "Tales";

    public int Mode { get; set; } = 1;

    public int IncrementMode { get; set; }

    public int NumCraftBatches { get; set; } = 16;

    public List<string> ToolsToCraft { get; set; } = [];

    public bool ClearStore { get; set; } = true;

    public static string ResolveDefaultToolToCraft(string? clientType)
    {
        return (clientType ?? string.Empty).Trim() switch
        {
            var value when value.Equals("YoStarEN", StringComparison.OrdinalIgnoreCase)
                || value.Equals("EN", StringComparison.OrdinalIgnoreCase) => "Glow Stick",
            var value when value.Equals("YoStarJP", StringComparison.OrdinalIgnoreCase)
                || value.Equals("JP", StringComparison.OrdinalIgnoreCase) => "ケミカルライト",
            var value when value.Equals("YoStarKR", StringComparison.OrdinalIgnoreCase)
                || value.Equals("KR", StringComparison.OrdinalIgnoreCase) => "형광봉",
            var value when value.Equals("txwy", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Txwy", StringComparison.OrdinalIgnoreCase) => "螢光棒",
            _ => "荧光棒",
        };
    }
}
