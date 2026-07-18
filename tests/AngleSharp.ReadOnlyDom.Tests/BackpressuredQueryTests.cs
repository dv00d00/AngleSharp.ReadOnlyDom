#if NET10_0
using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using AngleSharp.ReadOnlyDom.Streaming;
using AngleSharp.ReadOnlyDom.Streaming.Query;

namespace AngleSharp.Readonly.Tests;

public sealed class BackpressuredQueryTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DirectPipeWriterOutputPreservesBackpressure(bool encoded)
    {
        var input = new Pipe(new PipeOptions(useSynchronizationContext: false));
        var output = new Pipe(
            new PipeOptions(
                pauseWriterThreshold: 64,
                resumeWriterThreshold: 32,
                minimumSegmentSize: 16,
                useSynchronizationContext: false
            )
        );
        var state = new DirectOutput(output.Writer);
        var plan = StreamQuery
            .For<DirectOutput>("html")
            .OnText(static (ref DirectOutput destination, ReadOnlySpan<byte> text) => destination.Append(text))
            .Compile();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var expected = string.Concat(Enumerable.Repeat("abcdef café—uvwxyz012345", 128));
        var sourceEncoding = encoded ? Encoding.GetEncoding(1252) : Encoding.UTF8;
        var html = sourceEncoding.GetBytes($"<html><body><p>{expected}</p></body></html>");
        var execution = ExecuteAndCompleteOutputAsync();

        await input.Writer.WriteAsync(html);
        await input.Writer.CompleteAsync();

        var firstRead = await output.Reader.ReadAsync();
        await Assert.That(execution.IsCompleted).IsFalse();

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

        await execution;
        await input.Reader.CompleteAsync();
        await output.Reader.CompleteAsync();

        await Assert.That(Encoding.UTF8.GetString(received.WrittenSpan)).IsEqualTo(expected);

        async Task<DirectOutput> ExecuteAndCompleteOutputAsync()
        {
            try
            {
                return encoded
                    ? await plan.ExecuteEncodedAsync(
                        input.Reader,
                        output.Writer,
                        HtmlInputEncoding.Known(sourceEncoding),
                        state,
                        flushThreshold: 32,
                        inputSliceSize: 16
                    )
                    : await plan.ExecuteAsync(
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

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SlowOutputStopsInputDrainAndResumesWithoutChangingBytes(bool encoded)
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
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var expected = string.Concat(Enumerable.Repeat("abcdef café—uvwxyz012345", 128));
        var sourceEncoding = encoded ? Encoding.GetEncoding(1252) : Encoding.UTF8;
        var html = sourceEncoding.GetBytes($"<html><body><p>{expected}</p></body></html>");
        var execution = ExecuteAndCompleteOutputAsync();

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
        await Assert.That(finalState.MaximumPublishableBytes).IsLessThanOrEqualTo(48);

        async Task<TestOutput> ExecuteAndCompleteOutputAsync()
        {
            try
            {
                return encoded
                    ? await plan.ExecuteEncodedBackpressuredAsync(
                        input.Reader,
                        output.Writer,
                        HtmlInputEncoding.Known(sourceEncoding),
                        state,
                        flushThreshold: 32,
                        inputSliceSize: 16
                    )
                    : await plan.ExecuteBackpressuredAsync(
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

    private sealed class TestOutput : IUtf8PublishSource
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();

        public ReadOnlyMemory<byte> PublishableUtf8 => _buffer.WrittenMemory;

        internal int MaximumPublishableBytes { get; private set; }

        internal void Append(ReadOnlySpan<byte> value)
        {
            value.CopyTo(_buffer.GetSpan(value.Length));
            _buffer.Advance(value.Length);
            MaximumPublishableBytes = Math.Max(MaximumPublishableBytes, _buffer.WrittenCount);
        }

        public void AdvancePublished(int bytes)
        {
            if (bytes != _buffer.WrittenCount)
                throw new InvalidOperationException("The test output expects the complete publishable prefix to flush.");
            _buffer.Clear();
        }
    }

    private sealed class DirectOutput(PipeWriter writer)
    {
        internal void Append(ReadOnlySpan<byte> value)
        {
            value.CopyTo(writer.GetSpan(value.Length));
            writer.Advance(value.Length);
        }
    }
}
#endif
