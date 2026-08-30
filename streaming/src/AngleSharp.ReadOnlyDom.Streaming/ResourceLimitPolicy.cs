namespace AngleSharp.ReadOnlyDom.Streaming;

internal interface IResourceLimitPolicy
{
    static abstract Boolean Enabled { get; }
}

internal readonly struct EnforcedResourceLimits : IResourceLimitPolicy
{
    public static Boolean Enabled => true;
}

internal readonly struct UnboundedResources : IResourceLimitPolicy
{
    public static Boolean Enabled => false;
}
