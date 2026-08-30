using System.Buffers;

namespace AngleSharp.ReadOnlyDom.Streaming.Query.Rewriting;

/// <summary>Collects element mutations for a whole-buffer rewrite.</summary>
internal sealed class Utf8RewriteCollector : HtmlRewriteCollectorBase
{
    internal void WriteTo(ReadOnlySpan<byte> source, IBufferWriter<byte> output)
    {
        var segments = GetSegments(source);
        while (segments.MoveNext())
            Write(output, segments.Current);
    }

    internal void WriteTo<TState>(ReadOnlySpan<byte> source, ref TState state, RewriteSegmentSink<TState> sink)
    {
        var segments = GetSegments(source);
        while (segments.MoveNext())
            sink(ref state, segments.Current);
    }

    private SegmentEnumerator GetSegments(ReadOnlySpan<byte> source) => new(BuildEdits(source), source);

    private List<BufferedEdit> BuildEdits(ReadOnlySpan<byte> source)
    {
        var edits = new List<BufferedEdit>(Mutations.Count * 3 + TextMutations.Count);
        foreach (var mutation in Mutations)
        {
            if (mutation.Ignored)
                continue;
            if (mutation.Disposition is ElementDisposition.Remove or ElementDisposition.Replace)
            {
                var end = mutation.CanHaveContent ? mutation.EndEnd : mutation.SourceEnd;
                edits.Add(
                    new BufferedEdit(mutation.SourceStart, end, BuildWholeReplacement(mutation), mutation.StartSequence)
                );
                continue;
            }

            if (
                mutation.Before is { Count: > 0 }
                || (!mutation.CanHaveContent && mutation.After is { Count: > 0 })
                || mutation.Prepend is { Count: > 0 }
                || mutation.InnerReplacement is not null
                || mutation.Disposition == ElementDisposition.Unwrap
                || mutation.ChangesStartTag
            )
            {
                edits.Add(
                    new BufferedEdit(
                        mutation.SourceStart,
                        mutation.SourceEnd,
                        BuildStartReplacement(source, mutation),
                        mutation.StartSequence
                    )
                );
            }

            if (mutation.SuppressInnerContent && mutation.EndStart >= mutation.SourceEnd)
                edits.Add(new BufferedEdit(mutation.SourceEnd, mutation.EndStart, [], mutation.StartSequence));

            if (
                mutation.HasExplicitEndTag
                && (
                    mutation.Append is { Count: > 0 }
                    || mutation.After is { Count: > 0 }
                    || mutation.Disposition == ElementDisposition.Unwrap
                )
            )
            {
                edits.Add(
                    new BufferedEdit(
                        mutation.EndStart,
                        mutation.EndEnd,
                        BuildEndReplacement(source, mutation),
                        mutation.EndSequence
                    )
                );
            }
        }
        foreach (var mutation in TextMutations)
        {
            edits.Add(
                new BufferedEdit(
                    mutation.SourceStart,
                    mutation.SourceEnd,
                    BuildTextReplacement(source, mutation),
                    mutation.Sequence
                )
            );
        }
        edits.Sort(
            static (left, right) =>
            {
                var byStart = left.Start.CompareTo(right.Start);
                return byStart != 0 ? byStart : left.Sequence.CompareTo(right.Sequence);
            }
        );
        return edits;
    }

    private static byte[] BuildTextReplacement(ReadOnlySpan<byte> source, HtmlTextMutation mutation)
    {
        var output = new ArrayBufferWriter<byte>();
        WriteForward(output, mutation.Before);
        if (mutation.Removed)
            Write(output, mutation.Replacement ?? []);
        else
            Write(output, source[checked((int)mutation.SourceStart)..checked((int)mutation.SourceEnd)]);
        WriteReverse(output, mutation.After);
        return output.WrittenSpan.ToArray();
    }

    private static byte[] BuildWholeReplacement(HtmlElementMutation mutation)
    {
        var output = new ArrayBufferWriter<byte>();
        WriteForward(output, mutation.Before);
        if (mutation.Disposition == ElementDisposition.Replace)
            Write(output, mutation.Replacement!);
        if (!mutation.CanHaveContent || mutation.HasExplicitEndTag)
            WriteReverse(output, mutation.After);
        return output.WrittenSpan.ToArray();
    }

    private static byte[] BuildStartReplacement(ReadOnlySpan<byte> source, HtmlElementMutation mutation)
    {
        var output = new ArrayBufferWriter<byte>();
        WriteForward(output, mutation.Before);
        if (mutation.Disposition != ElementDisposition.Unwrap)
        {
            var tag = source[checked((int)mutation.SourceStart)..checked((int)mutation.SourceEnd)];
            if (mutation.ChangesStartTag)
                Write(output, StartTagMutationWriter.Rewrite(tag, mutation));
            else
                Write(output, tag);
        }
        WriteReverse(output, mutation.Prepend);
        if (mutation.InnerReplacement is not null)
            Write(output, mutation.InnerReplacement);
        if (!mutation.CanHaveContent)
            WriteReverse(output, mutation.After);
        return output.WrittenSpan.ToArray();
    }

    private static byte[] BuildEndReplacement(ReadOnlySpan<byte> source, HtmlElementMutation mutation)
    {
        var output = new ArrayBufferWriter<byte>();
        WriteForward(output, mutation.Append);
        if (mutation.Disposition != ElementDisposition.Unwrap)
            Write(output, source[checked((int)mutation.EndStart)..checked((int)mutation.EndEnd)]);
        WriteReverse(output, mutation.After);
        return output.WrittenSpan.ToArray();
    }

    internal static bool IsHtmlSpace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\f' or (byte)'\r';

    internal static void WriteForward(IBufferWriter<byte> output, List<byte[]>? values)
    {
        if (values is null)
            return;
        foreach (var value in values)
            Write(output, value);
    }

    internal static void WriteReverse(IBufferWriter<byte> output, List<byte[]>? values)
    {
        if (values is null)
            return;
        for (var index = values.Count - 1; index >= 0; index--)
            Write(output, values[index]);
    }

    internal static void Write(IBufferWriter<byte> output, ReadOnlySpan<byte> value)
    {
        value.CopyTo(output.GetSpan(value.Length));
        output.Advance(value.Length);
    }

    private readonly record struct BufferedEdit(long Start, long End, byte[] Replacement, int Sequence);

    private ref struct SegmentEnumerator(List<BufferedEdit> edits, ReadOnlySpan<byte> source)
    {
        private readonly List<BufferedEdit> _edits = edits;
        private readonly ReadOnlySpan<byte> _source = source;
        private int _index;
        private int _cursor;
        private byte[]? _pendingReplacement;
        private bool _tailEmitted;

        internal ReadOnlySpan<byte> Current { get; private set; }

        internal bool MoveNext()
        {
            if (_pendingReplacement is not null)
            {
                Current = _pendingReplacement;
                _pendingReplacement = null;
                return true;
            }

            while (_index < _edits.Count)
            {
                var edit = _edits[_index++];
                var start = checked((int)edit.Start);
                var end = checked((int)edit.End);
                if (start < _cursor || start > _source.Length || end < start || end > _source.Length)
                    continue;

                var untouched = _source[_cursor..start];
                _cursor = end;
                if (edit.Replacement.Length != 0)
                    _pendingReplacement = edit.Replacement;
                if (!untouched.IsEmpty)
                {
                    Current = untouched;
                    return true;
                }
                if (_pendingReplacement is not null)
                {
                    Current = _pendingReplacement;
                    _pendingReplacement = null;
                    return true;
                }
            }

            if (_tailEmitted || _cursor == _source.Length)
                return false;
            _tailEmitted = true;
            Current = _source[_cursor..];
            return true;
        }
    }
}
