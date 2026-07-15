namespace AngleSharp.ReadOnlyDom.Streaming;

internal sealed class Utf8HtmlTokenizerStateMetrics
{
    private readonly long[] _byteVisits;
    private readonly long[] _runs;
    private readonly int[] _maximumRunLengths;
    private int _lastState = -1;
    private int _currentRunLength;

    public Utf8HtmlTokenizerStateMetrics(int stateCount)
    {
        _byteVisits = new long[stateCount];
        _runs = new long[stateCount];
        _maximumRunLengths = new int[stateCount];
    }

    public void Record(int state, int byteCount)
    {
        if (byteCount == 0)
            return;

        _byteVisits[state] += byteCount;
        if (_lastState == state)
        {
            _currentRunLength += byteCount;
        }
        else
        {
            FinishRun();
            _lastState = state;
            _currentRunLength = byteCount;
            _runs[state]++;
        }

        if (_currentRunLength > _maximumRunLengths[state])
            _maximumRunLengths[state] = _currentRunLength;
    }

    public IReadOnlyList<Utf8HtmlTokenizerStateMetric> Snapshot(IReadOnlyList<string> stateNames)
    {
        var result = new List<Utf8HtmlTokenizerStateMetric>();
        for (var state = 0; state < _byteVisits.Length; state++)
        {
            if (_byteVisits[state] == 0)
                continue;

            result.Add(
                new Utf8HtmlTokenizerStateMetric(
                    stateNames[state],
                    _byteVisits[state],
                    _runs[state],
                    _maximumRunLengths[state]
                )
            );
        }

        result.Sort(static (left, right) => right.ByteVisits.CompareTo(left.ByteVisits));
        return result;
    }

    private void FinishRun()
    {
        if (_lastState >= 0 && _currentRunLength > _maximumRunLengths[_lastState])
            _maximumRunLengths[_lastState] = _currentRunLength;
    }
}

internal readonly record struct Utf8HtmlTokenizerStateMetric(
    string State,
    long ByteVisits,
    long Runs,
    int MaximumRunLength
);
