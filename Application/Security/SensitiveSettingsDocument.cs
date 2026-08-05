using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MAAUnified.Application.Security;

public sealed class SensitiveSettingsDocument
{
    public const int LatestFormatVersion = 1;

    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; } = LatestFormatVersion;

    [JsonPropertyName("storeId")]
    public string StoreId { get; set; } = string.Empty;

    [JsonPropertyName("revision")]
    public long Revision { get; set; }

    [JsonPropertyName("commitId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CommitId { get; set; }

    [JsonPropertyName("globalValues")]
    public Dictionary<string, JsonNode?> GlobalValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("profiles")]
    public Dictionary<string, Dictionary<string, JsonNode?>> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SensitiveSettingsEnvelope
{
    [JsonPropertyName("current")]
    public SensitiveSettingsDocument Current { get; set; } = new();

    [JsonPropertyName("previous")]
    public SensitiveSettingsDocument? Previous { get; set; }

    [JsonPropertyName("stores")]
    public Dictionary<string, SensitiveSettingsRevisionSet> Stores { get; set; } = new(StringComparer.Ordinal);
}

public sealed class SensitiveSettingsRevisionSet
{
    [JsonPropertyName("current")]
    public SensitiveSettingsDocument Current { get; set; } = new();

    [JsonPropertyName("previous")]
    public SensitiveSettingsDocument? Previous { get; set; }
}

public static class SensitiveSettingsEnvelopeStore
{
    public static SensitiveSettingsDocument FindCurrent(
        SensitiveSettingsEnvelope envelope,
        string storeId)
    {
        if (envelope.Stores.TryGetValue(storeId, out var revisions)
            && MatchesStore(revisions.Current, storeId))
        {
            return revisions.Current;
        }

        if (MatchesStore(envelope.Current, storeId))
        {
            return envelope.Current;
        }

        throw new SensitiveStorageException(
            SensitiveStorageErrorCode.RevisionNotFound,
            "Sensitive storage does not contain the requested store.");
    }

    public static SensitiveSettingsDocument FindRevision(
        SensitiveSettingsEnvelope envelope,
        string storeId,
        long revision)
    {
        if (envelope.Stores.TryGetValue(storeId, out var revisions))
        {
            if (Matches(revisions.Current, storeId, revision)) return revisions.Current;
            if (revisions.Previous is not null && Matches(revisions.Previous, storeId, revision)) return revisions.Previous;
        }

        if (Matches(envelope.Current, storeId, revision)) return envelope.Current;
        if (envelope.Previous is not null && Matches(envelope.Previous, storeId, revision)) return envelope.Previous;
        throw new SensitiveStorageException(
            SensitiveStorageErrorCode.RevisionNotFound,
            "Sensitive storage does not contain the requested revision.");
    }

    public static SensitiveSettingsEnvelope Merge(
        SensitiveSettingsEnvelope? envelope,
        SensitiveSettingsDocument document,
        long expectedRevision)
    {
        envelope ??= new SensitiveSettingsEnvelope();
        envelope.Stores ??= new Dictionary<string, SensitiveSettingsRevisionSet>(StringComparer.Ordinal);
        ImportLegacyCurrent(envelope);

        envelope.Stores.TryGetValue(document.StoreId, out var existing);
        var currentRevision = existing is null || string.IsNullOrWhiteSpace(existing.Current.StoreId)
            ? 0
            : existing.Current.Revision;
        if (currentRevision != expectedRevision)
        {
            throw new SensitiveStorageException(
                SensitiveStorageErrorCode.ActionRequired,
                $"Sensitive settings changed concurrently (expected revision {expectedRevision}, actual revision {currentRevision}). Reload before saving again.");
        }

        var previous = existing?.Current is { } current && !string.IsNullOrWhiteSpace(current.StoreId)
            ? current
            : existing?.Previous;
        envelope.Stores[document.StoreId] = new SensitiveSettingsRevisionSet
        {
            Current = document,
            Previous = previous,
        };
        envelope.Current = document;
        envelope.Previous = previous;
        return envelope;
    }

    public static SensitiveSettingsEnvelope ReplaceCurrent(
        SensitiveSettingsEnvelope? envelope,
        SensitiveSettingsDocument document,
        SensitiveSettingsDocument expectedCurrent,
        SensitiveSettingsDocument expectedPrevious)
    {
        if (envelope is null
            || !string.Equals(document.StoreId, expectedCurrent.StoreId, StringComparison.Ordinal)
            || document.Revision != expectedCurrent.Revision)
        {
            throw ConcurrentChange();
        }

        envelope.Stores ??= new Dictionary<string, SensitiveSettingsRevisionSet>(StringComparer.Ordinal);
        ImportLegacyCurrent(envelope);
        if (!envelope.Stores.TryGetValue(document.StoreId, out var existing)
            || existing.Previous is null
            || !DocumentsMatchExactly(existing.Current, expectedCurrent)
            || !DocumentsMatchExactly(existing.Previous, expectedPrevious))
        {
            throw ConcurrentChange();
        }

        envelope.Stores[document.StoreId] = new SensitiveSettingsRevisionSet
        {
            Current = document,
            Previous = existing.Previous,
        };
        envelope.Current = document;
        envelope.Previous = existing.Previous;
        return envelope;
    }

    private static void ImportLegacyCurrent(SensitiveSettingsEnvelope envelope)
    {
        if (!string.IsNullOrWhiteSpace(envelope.Current.StoreId)
            && !envelope.Stores.ContainsKey(envelope.Current.StoreId))
        {
            envelope.Stores[envelope.Current.StoreId] = new SensitiveSettingsRevisionSet
            {
                Current = envelope.Current,
                Previous = envelope.Previous,
            };
        }
    }

    private static bool Matches(SensitiveSettingsDocument document, string storeId, long revision)
        => string.Equals(document.StoreId, storeId, StringComparison.Ordinal) && document.Revision == revision;

    private static bool MatchesStore(SensitiveSettingsDocument document, string storeId)
        => string.Equals(document.StoreId, storeId, StringComparison.Ordinal)
           && document.Revision > 0;

    private static bool DocumentsMatchExactly(
        SensitiveSettingsDocument left,
        SensitiveSettingsDocument right)
    {
        return left.FormatVersion == right.FormatVersion
               && string.Equals(left.StoreId, right.StoreId, StringComparison.Ordinal)
               && left.Revision == right.Revision
               && string.Equals(left.CommitId, right.CommitId, StringComparison.Ordinal)
               && ValuesMatch(left.GlobalValues, right.GlobalValues)
               && ProfilesMatch(left.Profiles, right.Profiles);
    }

    private static bool ProfilesMatch(
        IReadOnlyDictionary<string, Dictionary<string, JsonNode?>> left,
        IReadOnlyDictionary<string, Dictionary<string, JsonNode?>> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var (profileName, leftValues) in left)
        {
            if (!right.TryGetValue(profileName, out var rightValues)
                || !ValuesMatch(leftValues, rightValues))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ValuesMatch(
        IReadOnlyDictionary<string, JsonNode?> left,
        IReadOnlyDictionary<string, JsonNode?> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var (key, leftValue) in left)
        {
            if (!right.TryGetValue(key, out var rightValue)
                || !JsonNode.DeepEquals(leftValue, rightValue))
            {
                return false;
            }
        }

        return true;
    }

    private static SensitiveStorageException ConcurrentChange()
        => new(
            SensitiveStorageErrorCode.ActionRequired,
            "Sensitive settings changed concurrently. Reload before saving again.");
}
