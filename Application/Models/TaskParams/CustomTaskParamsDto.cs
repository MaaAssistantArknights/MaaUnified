using System.Text.Json.Nodes;

namespace MAAUnified.Application.Models.TaskParams;

public sealed class CustomTaskParamsDto
{
    public List<string> TaskNames { get; set; } = [];

    /// <summary>
    /// 任务附加参数，如像素画的 <c>params.pixel_paint.groups</c>。
    /// 原样透传给 MaaCore（WPF <c>AsstCustomTask.Params</c>，<c>1f39c29587</c>）；
    /// 为空时不写 <c>params</c> 键，与旧配置保持一致的载荷。
    /// </summary>
    public JsonObject? Params { get; set; }
}
