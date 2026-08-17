namespace MAAUnified.Tests;

// 写入 ToolboxAssetCatalog 依赖的最小资源文件（条目值取自 MAA resource 的
// item_index.json / battle_data.json，仅保留目录消费的字段），配合
// ToolboxAssetCatalog.PushTestBaseDirectoriesForTests 注入，使工具箱测试
// 不依赖运行环境恰好存在的 resource/ 目录（standalone checkout 没有它，
// 仓库的 Actions 也未启用，缺失时失败无人发现）。
internal static class ToolboxTestResourceFiles
{
    public static void WriteTo(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "resource"));
        File.WriteAllText(
            Path.Combine(root, "resource", "item_index.json"),
            """
            {
              "2001": { "name": "基础作战记录", "classifyType": "MATERIAL", "sortId": 70004 },
              "3301": { "name": "技巧概要·卷1", "classifyType": "MATERIAL", "sortId": 80003 },
              "30011": { "name": "源岩", "classifyType": "MATERIAL", "sortId": 100040 },
              "30012": { "name": "固源岩", "classifyType": "MATERIAL", "sortId": 100039 },
              "30021": { "name": "代糖", "classifyType": "MATERIAL", "sortId": 100052 },
              "30041": { "name": "异铁碎片", "classifyType": "MATERIAL", "sortId": 100056 },
              "30042": { "name": "异铁", "classifyType": "MATERIAL", "sortId": 100055 },
              "30115": { "name": "聚合剂", "classifyType": "MATERIAL", "sortId": 100006 },
              "30125": { "name": "双极纳米片", "classifyType": "MATERIAL", "sortId": 100005 }
            }
            """);
        File.WriteAllText(
            Path.Combine(root, "resource", "battle_data.json"),
            """
            {
              "chars": {
                "char_003_kalts": {
                  "name": "凯尔希",
                  "name_en": "Kal'tsit",
                  "name_jp": "ケルシー",
                  "name_kr": "켈시",
                  "name_tw": "凱爾希",
                  "position": "RANGED",
                  "profession": "MEDIC",
                  "rarity": 6
                },
                "char_102_texas": {
                  "name": "德克萨斯",
                  "name_en": "Texas",
                  "name_jp": "テキサス",
                  "name_kr": "텍사스",
                  "name_tw": "德克薩斯",
                  "position": "MELEE",
                  "profession": "PIONEER",
                  "rarity": 5
                }
              }
            }
            """);
    }
}
