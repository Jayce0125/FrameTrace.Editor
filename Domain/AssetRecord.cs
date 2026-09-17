namespace FrameTrace.Editor.Domain;

public sealed class AssetRecord
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string MediaType { get; init; }

    public required string LocalPath { get; init; }

    public required string FolderName { get; init; }

    public string? PosterPath { get; init; }

    public required string Prompt { get; init; }

    public string? ContentHash { get; init; }

    public IReadOnlyList<ReferenceResource> References { get; init; } = [];

    public AssetMetadata Meta { get; init; } = new();

    public string? Author { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public int? Rating { get; init; }

    public string? ReviewStatus { get; init; }
}

public sealed class ReferenceResource
{
    public required string Type { get; init; }

    public required string LocalPath { get; init; }
}

public sealed class AssetMetadata
{
    public DateTimeOffset? Created { get; init; }

    public string? Quality { get; init; }

    public string? AspectRatio { get; init; }

    public long? Size { get; init; }

    public string? Feature { get; init; }
}