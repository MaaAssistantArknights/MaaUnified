namespace MAAUnified.Application.Models;

public sealed record WindowsRelayManifest(
    string? Version,
    string? Channel,
    IReadOnlyList<WindowsRelayPackageEntry> Packages);

public sealed record WindowsRelayPackageEntry(
    string? Version,
    string? Channel,
    string Os,
    string Arch,
    string Url,
    string? Sha256 = null,
    long? Size = null,
    string? Name = null);
