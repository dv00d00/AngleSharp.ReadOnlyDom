namespace AngleSharp.ReadOnlyDom.Helpers;

public static class AuxHelpers
{
    public static bool ContainsI(this string src, string test)
    {
        return src.IndexOf(test, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static T? TryAt<T>(this IReadOnlyList<T> list, int index)
    {
        if (index < 0 || index >= list.Count)
            return default;

        return list[index];
    }

    public static bool IsNullOrWhiteSpace(this string? str)
    {
        return String.IsNullOrWhiteSpace(str);
    }
}
