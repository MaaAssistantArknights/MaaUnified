using System.Text.Json.Nodes;

namespace MAAUnified.Application.Models.TaskParams;

public sealed class SingleStepTaskParamsDto
{
    public string Type { get; set; } = string.Empty;

    public string Subtype { get; set; } = string.Empty;

    public JsonNode? Details { get; set; }
}
