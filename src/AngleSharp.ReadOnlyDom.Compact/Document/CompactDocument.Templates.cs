namespace AngleSharp.ReadOnlyDom.Compact.Document;

public sealed partial class CompactDocument
{
    internal bool IsTemplate(int handle)
    {
        if (!_hasTemplates)
            return false;
        foreach (var boundary in _templateBoundaries)
            if (boundary.Handle == handle)
                return true;
        return false;
    }

    internal bool TryGetTemplateContent(int handle, out int contentStart)
    {
        if (!_hasTemplates)
        {
            contentStart = -1;
            return false;
        }

        foreach (var boundary in _templateBoundaries)
        {
            if (boundary.Handle != handle)
                continue;
            contentStart = boundary.ContentStart;
            return contentStart >= 0;
        }

        contentStart = -1;
        return false;
    }

    internal bool TryGetContainingTemplateContentEnd(int handle, out int contentEnd)
    {
        contentEnd = -1;
        if (!_hasTemplates)
            return false;
        foreach (var boundary in _templateBoundaries)
            if (handle >= boundary.ContentStart && handle < boundary.ContentEnd)
                contentEnd = Math.Max(contentEnd, boundary.ContentEnd);
        return contentEnd >= 0;
    }

    internal bool IsInSameTreeScope(int first, int second)
    {
        if (!_hasTemplates)
            return true;
        foreach (var boundary in _templateBoundaries)
        {
            var firstInContent = first >= boundary.ContentStart && first < boundary.ContentEnd;
            var secondInContent = second >= boundary.ContentStart && second < boundary.ContentEnd;
            if (firstInContent != secondInContent)
                return false;
        }

        return true;
    }
}
