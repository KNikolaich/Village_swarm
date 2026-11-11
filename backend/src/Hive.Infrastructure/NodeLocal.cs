using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Hive.Infrastructure;

/// <summary>Endpoint metadata: the route (or a whole group) belongs to this node alone, so an observer node still accepts changes there.</summary>
public sealed class NodeLocalAttribute : Attribute;

public static class NodeLocalExtensions
{
    /// <summary>Marks the route or group as node-local: allowed to change data on a read-only (observer) node.</summary>
    public static TBuilder NodeLocal<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new NodeLocalAttribute());

    public static bool IsNodeLocal(this Endpoint? endpoint) => endpoint?.Metadata.GetMetadata<NodeLocalAttribute>() is not null;
}
