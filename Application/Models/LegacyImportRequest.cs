using System.Text.Json.Nodes;
using MAAUnified.Application.Configuration;

namespace MAAUnified.Application.Models;

public sealed record LegacyImportRequest(
    LegacyConfigSnapshot Snapshot,
    ImportSource Source,
    bool ManualImport,
    bool AllowPartialImport = true,
    IReadOnlyDictionary<string, JsonNode?>? FallbackGlobalValues = null);
