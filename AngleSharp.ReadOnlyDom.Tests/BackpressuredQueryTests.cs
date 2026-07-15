#if NET10_0
using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using AngleSharp.ReadOnlyDom.Streaming.Utf8Stream;

namespace AngleSharp.Readonly.Tests;

public sealed class BackpressuredQueryTests
{
    [Test]
    public async Task SlowOutputStopsInputDrainAndResumesWithoutChangingBytes()
    {
        var root = StreamQuery.For<TestOutput>("html").OnText(static (ref output, text) => output.Append(text));
        var plan = root.Compile();
        var input = new Pipe(new PipeOptions(useSynchronizationContext: false));
        var output = new Pipe(
            new PipeOptions(
                pauseWriterThreshold: 64,
                resumeWriterThreshold: 32,
                minimumSegmentSize: 16,
                useSynchronizationContext: false
            )
        );
        var state = new TestOutput();
        var execution = ExecuteAndCompleteOutputAsync();
        var expected = string.Concat(Enumerable.Repeat("abcdefghijklmnopqrstuvwxyz012345", 128));
        var html = Encoding.UTF8.GetBytes($"<html><body><p>{expected}</p></body></html>");

        await input.Writer.WriteAsync(html);
        await input.Writer.CompleteAsync();

        var firstRead = await output.Reader.ReadAsync();
        await Assert.That(execution.IsCompleted).IsFalse();
        await Task.Delay(10);

        var received = new ArrayBufferWriter<byte>();
        Copy(firstRead.Buffer, received);
        output.Reader.AdvanceTo(firstRead.Buffer.End);
        var completed = firstRead.IsCompleted;
        while (!completed)
        {
            var read = await output.Reader.ReadAsync();
            Copy(read.Buffer, received);
            output.Reader.AdvanceTo(read.Buffer.End);
            completed = read.IsCompleted;
        }

        var finalState = await execution;
        await input.Reader.CompleteAsync();
        await output.Reader.CompleteAsync();

        await Assert.That(Encoding.UTF8.GetString(received.WrittenSpan)).IsEqualTo(expected);
        await Assert.That(finalState.MaximumCommittedBytes).IsLessThanOrEqualTo(48);

        async Task<TestOutput> ExecuteAndCompleteOutputAsync()
        {
            try
            {
                return await plan.ExecuteBackpressuredAsync(
                    input.Reader,
                    output.Writer,
                    state,
                    flushThreshold: 32,
                    inputSliceSize: 16
                );
            }
            finally
            {
                await output.Writer.CompleteAsync();
            }
        }
    }

    private static void Copy(ReadOnlySequence<byte> source, IBufferWriter<byte> destination)
    {
        foreach (var segment in source)
        {
            segment.Span.CopyTo(destination.GetSpan(segment.Length));
            destination.Advance(segment.Length);
        }
    }

    private sealed class TestOutput : ICommittedUtf8Output
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();

        public ReadOnlyMemory<byte> CommittedUtf8 => _buffer.WrittenMemory;

        internal int MaximumCommittedBytes { get; private set; }

        internal void Append(ReadOnlySpan<byte> value)
        {
            value.CopyTo(_buffer.GetSpan(value.Length));
            _buffer.Advance(value.Length);
            MaximumCommittedBytes = Math.Max(MaximumCommittedBytes, _buffer.WrittenCount);
        }

        public void AdvanceCommitted(int bytes)
        {
            if (bytes != _buffer.WrittenCount)
                throw new InvalidOperationException("The test output expects the complete committed prefix to flush.");
            _buffer.Clear();
        }
    }
}
#endif
