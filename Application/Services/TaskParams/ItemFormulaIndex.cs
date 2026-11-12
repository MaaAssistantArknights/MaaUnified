using System.Text.Json.Nodes;
using MAAUnified.Compat.Runtime;

namespace MAAUnified.Application.Services.TaskParams;

/// <summary>
/// 物品合成配方索引（<c>resource/item_index.json</c> 的 <c>formula</c> 字段）。
/// 库存保持任务用它把双芯片按配方折算成芯片组库存
/// （WPF <c>ItemListHelper.GetFormulaProducts</c> / <c>IsDualChip</c>，
/// dev-v2 be9cc84fd2）。资源缺失时返回空索引，折算量按 0 计，不影响任务运行。
/// </summary>
public static class ItemFormulaIndex
{
    /// <summary>
    /// 芯片助剂（双芯片配方必需品）；命中该材料的产物即双芯片。
    /// </summary>
    public const string DualChipCatalystItemId = "32001";

    private static readonly object Gate = new();
    private static readonly Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 读取配方索引：产物物品 id → (材料物品 id → 配方消耗数量)。
    /// </summary>
    /// <param name="baseDirectory">资源根目录；缺省用运行时根目录。</param>
    /// <returns>配方索引；无资源文件时为空索引。</returns>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Load(string? baseDirectory = null)
    {
        var root = string.IsNullOrWhiteSpace(baseDirectory)
            ? RuntimeLayout.ResolveRuntimeBaseDirectory()
            : Path.GetFullPath(baseDirectory);

        lock (Gate)
        {
            if (Cache.TryGetValue(root, out var cached))
            {
                return cached;
            }

            var loaded = LoadCore(root);
            Cache[root] = loaded;
            return loaded;
        }
    }

    /// <summary>
    /// 计算芯片组库存按双芯片配方折算后的增量：仓库中的双芯片数量 × 配方消耗的芯片组数量
    /// （当前数据每个双芯片折算为 2 个芯片组）。非芯片组、非双芯片或配方缺失时为 0。
    /// </summary>
    /// <param name="dropId">计划目标物品 id。</param>
    /// <param name="depotCounts">识别缓存的库存数量表。</param>
    /// <param name="formulas">配方索引，见 <see cref="Load"/>。</param>
    /// <returns>折算增量。</returns>
    public static int GetDualChipConversion(
        string dropId,
        IReadOnlyDictionary<string, int> depotCounts,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> formulas)
    {
        if (string.IsNullOrWhiteSpace(dropId) || depotCounts.Count == 0 || formulas.Count == 0)
        {
            return 0;
        }

        var conversion = 0;
        foreach (var (productId, formula) in formulas)
        {
            if (!formula.TryGetValue(dropId, out var materialCount) || materialCount <= 0)
            {
                continue;
            }

            // 双芯片判定与 WPF 一致：配方含芯片助剂。
            if (!formula.ContainsKey(DualChipCatalystItemId))
            {
                continue;
            }

            if (depotCounts.TryGetValue(productId, out var count) && count > 0)
            {
                conversion += count * materialCount;
            }
        }

        return conversion;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> LoadCore(string root)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
        foreach (var path in EnumerateCandidatePaths(root))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject items)
                {
                    continue;
                }

                foreach (var (itemId, node) in items)
                {
                    if (node is not JsonObject item
                        || item["formula"] is not JsonObject formulaNode)
                    {
                        continue;
                    }

                    var formula = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (var (materialId, countNode) in formulaNode)
                    {
                        if (countNode is JsonValue value
                            && value.TryGetValue(out int count)
                            && count > 0)
                        {
                            formula[materialId] = count;
                        }
                    }

                    if (formula.Count > 0)
                    {
                        result[itemId] = formula;
                    }
                }
            }
            catch (Exception)
            {
                // 资源文件损坏时按无配方处理，不阻断任务。
                continue;
            }

            if (result.Count > 0)
            {
                return result;
            }
        }

        return result;
    }

    private static IEnumerable<string> EnumerateCandidatePaths(string root)
    {
        yield return Path.Combine(root, "resource", "item_index.json");

        var globalRoot = Path.Combine(root, "resource", "global");
        if (!Directory.Exists(globalRoot))
        {
            yield break;
        }

        string[] clientDirectories;
        try
        {
            clientDirectories = Directory.GetDirectories(globalRoot);
        }
        catch (Exception)
        {
            yield break;
        }

        Array.Sort(clientDirectories, StringComparer.OrdinalIgnoreCase);
        foreach (var clientDirectory in clientDirectories)
        {
            yield return Path.Combine(clientDirectory, "resource", "item_index.json");
        }
    }
}
