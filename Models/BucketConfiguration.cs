using System;
using System.Collections.Generic;

namespace BucketFlow.Models;

public record BucketConfiguration
{
    public required string Name { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public Dictionary<string, string> Metadata { get; init; } = new();
}

public record QueryParameters
{
    public Dictionary<string, string> Filters { get; init; } = new();
    public int? Limit { get; init; }
    public int? Offset { get; init; }
    public string? SortBy { get; init; }
    public bool SortDescending { get; init; }
}

public record ApiResponse<T>
{
    public bool Success { get; init; }
    public T? Data { get; init; }
    public string? Error { get; init; }
} 