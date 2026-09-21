namespace FrameTrace.Editor.Domain;

public sealed class AssetRecord
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string MediaType { get; init; } = string.Empty;

    public string LocalPath { get; init; } = string.Empty;

    public string FolderName { get; init; } = string.Empty;

    public string? PosterPath { get; init; }

    public string Prompt { get; init; } = string.Empty;

    public string? ContentHash { get; init; }

    public IReadOnlyList<ReferenceResource> References { get; init; } = Array.Empty<ReferenceResource>();

    public AssetMetadata Meta { get; init; } = new();

    public string? Author { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public int? Rating { get; init; }

    public string? ReviewStatus { get; init; }
}

public sealed class ReferenceResource
{
    public string Type { get; init; } = string.Empty;

    public string LocalPath { get; init; } = string.Empty;
}

public sealed class AssetMetadata
{
    public DateTimeOffset? Created { get; init; }

    public string? Quality { get; init; }

    public string? AspectRatio { get; init; }

    public long? Size { get; init; }

    public string? Feature { get; init; }
}