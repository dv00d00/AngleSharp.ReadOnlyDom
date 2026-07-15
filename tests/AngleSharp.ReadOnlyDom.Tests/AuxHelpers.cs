namespace AngleSharp.Readonly.Tests;

internal static class AuxHelpers
{
    public static bool IsNullOrWhiteSpace(this string? str)
    {
        return String.IsNullOrWhiteSpace(str);
    }
}
