namespace MAAUnified.Application.Models;

/// <summary>
/// 活动商店（<c>SS@Store@Begin</c>）黑名单预设关键词。
/// Core 与 WPF 都按游戏客户端语言匹配商品名，因此预设词随客户端切换；
/// 见 dev-v2 1bc83ba6e6 <c>ToolboxViewModel.StartEventShop</c>。
/// </summary>
public static class ToolboxMiniGameKeywords
{
    public sealed record EventShopKeywords(
        string DataSupplementInstrument,
        string DataSupplementStick,
        string Lmd,
        string FurniturePart);

    public static EventShopKeywords ResolveEventShopKeywords(string? clientType)
    {
        return NormalizeClientType(clientType) switch
        {
            "EN" => new("Data Supplement Instrument", "Data Supplement Stick", "LMD", "Furniture Part"),
            "JP" => new("データ補完マシン", "データ補完チップ", "龍門幣", "家具"),
            "KR" => new("데이터 리더기", "데이터 메모리", "용문폐", "가구 부품"),
            "Txwy" => new("數據增補儀", "數據增補條", "龍門幣", "傢俱"),
            _ => new("数据增补仪", "数据增补条", "龙门币", "家具"),
        };
    }

    private static string NormalizeClientType(string? clientType)
    {
        var value = (clientType ?? string.Empty).Trim();
        if (value.Equals("YoStarEN", StringComparison.OrdinalIgnoreCase)
            || value.Equals("EN", StringComparison.OrdinalIgnoreCase)
            || value.Equals("OfficialEN", StringComparison.OrdinalIgnoreCase))
        {
            return "EN";
        }

        if (value.Equals("YoStarJP", StringComparison.OrdinalIgnoreCase)
            || value.Equals("JP", StringComparison.OrdinalIgnoreCase)
            || value.Equals("OfficialJP", StringComparison.OrdinalIgnoreCase))
        {
            return "JP";
        }

        if (value.Equals("YoStarKR", StringComparison.OrdinalIgnoreCase)
            || value.Equals("KR", StringComparison.OrdinalIgnoreCase)
            || value.Equals("OfficialKR", StringComparison.OrdinalIgnoreCase))
        {
            return "KR";
        }

        return value.Equals("txwy", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Txwy", StringComparison.OrdinalIgnoreCase)
            || value.Equals("YoStarTW", StringComparison.OrdinalIgnoreCase)
            ? "Txwy"
            : "CN";
    }
}
