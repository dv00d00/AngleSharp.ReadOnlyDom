namespace AngleSharp.ReadOnlyDom.Compact.Arena;

internal sealed partial class Arena
{
    public void RemoveFromParent(int child)
    {
        if (_columns.Parents[child] >= 0)
            _requiresRemap = true;
        Detach(child);
    }

    public void RemoveChild(int parent, int child)
    {
        if (_columns.Parents[child] == parent)
        {
            _requiresRemap = true;
            Detach(child);
        }
    }

    public void ClearChildren(int parent)
    {
        if (_columns.ChildCounts[parent] != 0)
            _requiresRemap = true;
        var child = _columns.FirstChildren[parent];
        while (child >= 0)
        {
            var next = _columns.NextSiblings[child];
            _columns.Parents[child] = -1;
            _columns.PreviousSiblings[child] = -1;
            _columns.NextSiblings[child] = -1;
            _unattachedNodeCount++;
            child = next;
        }

        _columns.FirstChildren[parent] = -1;
        _columns.LastChildren[parent] = -1;
        _columns.ChildCounts[parent] = 0;
    }

    public void PopulateTemplate(int handle)
    {
        _columns.SetTemplateFirstChild(handle, _columns.FirstChildren[handle]);
        _columns.FirstChildren[handle] = -1;
        _columns.LastChildren[handle] = -1;
        _columns.ChildCounts[handle] = 0;
    }

    private void AppendChild(int parent, int child)
    {
        var previous = _columns.LastChildren[parent];
        _columns.Parents[child] = parent;
        _columns.PreviousSiblings[child] = previous;
        _columns.NextSiblings[child] = -1;
        if (previous >= 0)
            _columns.NextSiblings[previous] = child;
        else
            _columns.FirstChildren[parent] = child;
        _columns.LastChildren[parent] = child;
        _columns.ChildCounts[parent]++;
    }

    private void InsertBefore(int parent, int child, int next)
    {
        _requiresRemap = true;
        var previous = _columns.PreviousSiblings[next];
        _columns.Parents[child] = parent;
        _columns.PreviousSiblings[child] = previous;
        _columns.NextSiblings[child] = next;
        _columns.PreviousSiblings[next] = child;
        if (previous >= 0)
            _columns.NextSiblings[previous] = child;
        else
            _columns.FirstChildren[parent] = child;
        _columns.ChildCounts[parent]++;
    }

    private void Detach(int child)
    {
        var parent = _columns.Parents[child];
        if (parent < 0)
            return;
        var previous = _columns.PreviousSiblings[child];
        var next = _columns.NextSiblings[child];
        if (previous >= 0)
            _columns.NextSiblings[previous] = next;
        else
            _columns.FirstChildren[parent] = next;
        if (next >= 0)
            _columns.PreviousSiblings[next] = previous;
        else
            _columns.LastChildren[parent] = previous;
        _columns.Parents[child] = -1;
        _columns.PreviousSiblings[child] = -1;
        _columns.NextSiblings[child] = -1;
        _columns.ChildCounts[parent]--;
        _unattachedNodeCount++;
    }
}
