namespace AngleSharp.ReadOnlyDom.HackerNews.Upstream;

/// <summary>Raised when the sample refuses to dial a target rather than because the target failed.</summary>
internal sealed class UpstreamBlockedException(string message) : Exception(message);
