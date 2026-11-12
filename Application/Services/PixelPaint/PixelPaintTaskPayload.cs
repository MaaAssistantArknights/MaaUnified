using System.Text.Json.Nodes;

namespace MAAUnified.Application.Services.PixelPaint;

/// <summary>像素画参数校验结果。</summary>
public enum PixelPaintPayloadIssue
{
    /// <summary>无问题。</summary>
    None,

    /// <summary>没有任何可画格子（全白或未生成预览）。</summary>
    NoGroups,

    /// <summary>色板下标越界。</summary>
    ColorOutOfRange,

    /// <summary>格子坐标越界。</summary>
    PointOutOfRange,

    /// <summary>每格额外等待时长越界。</summary>
    GridDelayOutOfRange,
}

/// <summary>
/// 像素画的 Core 自定义任务参数（WPF <c>AsstProxy.AsstPixelPaint</c>，<c>1f39c29587</c>）：
/// <c>{"task_names":["MiniGame@PixelPaint@Begin"],"params":{"pixel_paint":{...}}}</c>，
/// 由 MaaCore <c>CustomTask::parse_and_register_pixel_paint</c> 读取。
/// </summary>
public static class PixelPaintTaskPayload
{
    /// <summary>Core 侧像素画入口任务名（资源只定义了 <c>@Begin</c>）。</summary>
    public const string TaskName = "MiniGame@PixelPaint@Begin";

    /// <summary>每格额外等待时长的上限（毫秒），与 WPF 数值控件一致。</summary>
    public const int MaxGridDelay = 500;

    /// <summary>
    /// 校验分组点列与绘制参数，返回首个问题。
    /// </summary>
    /// <param name="groups">按色分组后的点列。</param>
    /// <param name="gridDelay">每格额外等待（毫秒）。</param>
    /// <returns>校验结果。</returns>
    public static PixelPaintPayloadIssue Validate(IReadOnlyList<PixelPaintColorGroup>? groups, int gridDelay)
    {
        if (groups is null || groups.Count == 0)
        {
            return PixelPaintPayloadIssue.NoGroups;
        }

        if (gridDelay < 0 || gridDelay > MaxGridDelay)
        {
            return PixelPaintPayloadIssue.GridDelayOutOfRange;
        }

        foreach (var group in groups)
        {
            if (group.Color < 0 || group.Color >= PixelPaintPalette.ColorCount)
            {
                return PixelPaintPayloadIssue.ColorOutOfRange;
            }

            if (group.Points.Count == 0)
            {
                return PixelPaintPayloadIssue.NoGroups;
            }

            foreach (var point in group.Points)
            {
                if (point.X < 0 || point.X >= PixelPaintPalette.GridSize
                    || point.Y < 0 || point.Y >= PixelPaintPalette.GridSize)
                {
                    return PixelPaintPayloadIssue.PointOutOfRange;
                }
            }
        }

        return PixelPaintPayloadIssue.None;
    }

    /// <summary>
    /// 构造 Core 自定义任务参数。调用方应先经 <see cref="Validate"/> 校验。
    /// </summary>
    /// <param name="groups">按色分组后的点列。</param>
    /// <param name="swipe">是否启用滑动绘制（同色同行的连续格一次拖完）。</param>
    /// <param name="gridDelay">每格额外等待（毫秒）。</param>
    /// <returns>Core 任务参数对象。</returns>
    public static JsonObject Build(IReadOnlyList<PixelPaintColorGroup> groups, bool swipe, int gridDelay)
    {
        ArgumentNullException.ThrowIfNull(groups);

        var groupArray = new JsonArray();
        foreach (var group in groups)
        {
            var points = new JsonArray();
            foreach (var point in group.Points)
            {
                points.Add(new JsonArray(JsonValue.Create(point.X), JsonValue.Create(point.Y)));
            }

            groupArray.Add(new JsonObject
            {
                ["color"] = group.Color,
                ["points"] = points,
            });
        }

        return new JsonObject
        {
            ["task_names"] = new JsonArray(JsonValue.Create(TaskName)),
            ["params"] = new JsonObject
            {
                ["pixel_paint"] = new JsonObject
                {
                    ["swipe"] = swipe,
                    ["grid_delay"] = gridDelay,
                    ["groups"] = groupArray,
                },
            },
        };
    }

    /// <summary>
    /// 统计分组点列中的格子总数。
    /// </summary>
    /// <param name="groups">按色分组后的点列。</param>
    /// <returns>需绘制的格子总数。</returns>
    public static int CountPaintedCells(IReadOnlyList<PixelPaintColorGroup>? groups)
    {
        if (groups is null)
        {
            return 0;
        }

        var total = 0;
        foreach (var group in groups)
        {
            total += group.Points.Count;
        }

        return total;
    }

    /// <summary>
    /// 构造执行记录里的参数摘要（WPF 无同字段，这里与工具箱其他条目保持一致的可读摘要）。
    /// </summary>
    /// <param name="groups">按色分组后的点列。</param>
    /// <param name="swipe">是否启用滑动绘制。</param>
    /// <param name="gridDelay">每格额外等待（毫秒）。</param>
    /// <returns>参数摘要。</returns>
    public static string BuildParameterSummary(IReadOnlyList<PixelPaintColorGroup>? groups, bool swipe, int gridDelay)
    {
        return $"taskName={TaskName};cells={CountPaintedCells(groups)};colors={groups?.Count ?? 0};swipe={swipe.ToString().ToLowerInvariant()};gridDelay={gridDelay}";
    }
}
