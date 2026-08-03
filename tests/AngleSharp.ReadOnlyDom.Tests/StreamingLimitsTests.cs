#if NET10_0
using System.IO.Pipelines;
using System.Text;
using AngleSharp.ReadOnlyDom.Streaming;
using AngleSharp.ReadOnlyDom.Streaming.Public;
using AngleSharp.ReadOnlyDom.Streaming.Utf8Tokenizer;

namespace AngleSharp.Readonly.Tests;

public sealed class StreamingLimitsTests
{
    private static readonly HtmlStreamingLimits SmallScratch = new(
        maximumBufferedTokenBytes: 64,
        maximumNestingDepth: 100,
        maximumInputBytes: 1_000_000,
        maximumQueryCaptureBytes: 1_000_000
    );

    public static IEnumerable<string> DegenerateTokens()
    {
        yield return "<!--" + new string('x', 1000);
        yield return "<" + new string('a', 1000);
        yield return "<a value='" + new string('x', 1000);
        yield return "<!DOCTYPE html PUBLIC \"" + new string('x', 1000);
        yield return "<script></" + new string('x', 1000);
    }

    [Test]
    [MethodDataSource(nameof(DegenerateTokens))]
    public async Task DegenerateTokenScratchIsBoundedAcrossChunkBoundaries(string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        foreach (var chunkSize in new[] { 1, 7, bytes.Length })
        {
            var tokenizer = new Utf8HtmlTokenizer(new NullSink(), SmallScratch);
            var error = Capture(() =>
            {
                for (var offset = 0; offset < bytes.Length; offset += chunkSize)
                    tokenizer.Write(bytes.AsSpan(offset, Math.Min(chunkSize, bytes.Length - offset)));
                tokenizer.Complete();
            });

            await Assert.That(error.Limit).IsEqualTo(HtmlStreamingLimit.BufferedTokenBytes);
            await Assert.That(error.Allowed).IsEqualTo(64);
            await Assert.That(error.Observed).IsGreaterThan(64);
        }
    }

    [Test]
    public async Task ScratchAccountingDoesNotAccumulateAcrossClearedTokenBuffers()
    {
        const string fragment = "<alpha first=123456 second=abcdef></alpha><beta third=uvwxyz><!--ok--></beta>";
        var tokenizer = new Utf8HtmlTokenizer(new NullSink(), SmallScratch);

        for (var iteration = 0; iteration < 1000; iteration++)
            tokenizer.Write(Encoding.UTF8.GetBytes(fragment));
        tokenizer.Complete();

        await Assert.That(tokenizer.Counters.MaximumBufferedTokenBytes).IsLessThanOrEqualTo(64);
    }

    [Test]
    public async Task InputBudgetCountsEachRawByteOnceAcrossWrites()
    {
        var limits = Limits(input: 10);
        var tokenizer = new Utf8HtmlTokenizer(new NullSink(), limits);
        tokenizer.Write("1234567"u8);

        var error = Capture(() => tokenizer.Write("8901234"u8));

        await Assert.That(error.Limit).IsEqualTo(HtmlStreamingLimit.InputBytes);
        await Assert.That(error.Allowed).IsEqualTo(10);
        await Assert.That(error.Observed).IsEqualTo(14);
    }

    [Test]
    public async Task KnownEncodingBudgetCountsWireBytesNotTranscodedUtf8()
    {
        var bytes = Encoding.Latin1.GetBytes("<p>café</p>");
        var reader = PipeReader.Create(new MemoryStream(bytes));
        var plan = StreamQuery.For<TestState>("p").Compile();

        var error = await CaptureAsync(async () =>
            await plan.ExecuteEncodedAsync(
                reader,
                HtmlInputEncoding.Known(Encoding.Latin1),
                new TestState(),
                limits: Limits(input: bytes.Length - 1)
            )
        );
        await reader.CompleteAsync();

        await Assert.That(error.Limit).IsEqualTo(HtmlStreamingLimit.InputBytes);
        await Assert.That(error.Observed).IsEqualTo(bytes.Length);
    }

    [Test]
    public async Task RawPipeFailureReleasesTheOutstandingRead()
    {
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync("<p>too much input</p>"u8.ToArray());
        await pipe.Writer.CompleteAsync();
        var plan = StreamQuery.For<TestState>("p").Compile();

        var error = await CaptureAsync(async () =>
            await plan.ExecuteAsync(pipe.Reader, new TestState(), limits: Limits(input: 5))
        );
        var remaining = await pipe.Reader.ReadAsync();
        pipe.Reader.AdvanceTo(remaining.Buffer.End);
        await pipe.Reader.CompleteAsync();

        await Assert.That(error.Limit).IsEqualTo(HtmlStreamingLimit.InputBytes);
        await Assert.That(remaining.Buffer.IsEmpty).IsTrue();
    }

    [Test]
    public async Task DepthLimitFailsBeforeCurrentElementCallback()
    {
        var starts = 0;
        var plan = StreamQuery.For<TestState>("div").OnStart((ref TestState _, in Element _) => starts++).Compile();

        var error = Capture(() => plan.Execute("<div><div><div><div>"u8, new TestState(), Limits(depth: 3)));

        await Assert.That(error.Limit).IsEqualTo(HtmlStreamingLimit.NestingDepth);
        await Assert.That(error.Observed).IsEqualTo(4);
        await Assert.That(starts).IsEqualTo(3);
    }

    [Test]
    public async Task NestedCompletedCapturesShareOneAggregateBudget()
    {
        var completed = 0;
        var plan = StreamQuery
            .For<TestState>("div")
            .OnTextContent((ref TestState _, in CompletedElement _) => completed++)
            .Compile();

        var error = Capture(() =>
            plan.Execute("<div><div><div>1234567890</div></div></div>"u8, new TestState(), Limits(capture: 20))
        );

        await Assert.That(error.Limit).IsEqualTo(HtmlStreamingLimit.QueryCaptureBytes);
        await Assert.That(error.Allowed).IsEqualTo(20);
        await Assert.That(error.Observed).IsEqualTo(30);
        await Assert.That(completed).IsEqualTo(0);
    }

    [Test]
    public async Task PendingAndCompletedAttributeCopiesAreBothBudgetedWhileLive()
    {
        var completed = 0;
        var plan = StreamQuery
            .For<TestState>("p")
            .OnClose((ref TestState _, in CompletedElement _) => completed++, "data-value")
            .Compile();

        var error = Capture(() => plan.Execute("<p data-value='1234567890'>"u8, new TestState(), Limits(capture: 15)));

        await Assert.That(error.Limit).IsEqualTo(HtmlStreamingLimit.QueryCaptureBytes);
        await Assert.That(error.Observed).IsEqualTo(20);
        await Assert.That(completed).IsEqualTo(0);
    }

    [Test]
    public async Task UnlimitedPreservesOptOut()
    {
        var tokenizer = new Utf8HtmlTokenizer(new NullSink(), HtmlStreamingLimits.Unlimited);
        tokenizer.Write(Encoding.UTF8.GetBytes("<!--" + new string('x', 1024) + "-->"));
        tokenizer.Complete();

        await Assert.That(tokenizer.Counters.BytesConsumed).IsGreaterThan(1024);
    }

    private static HtmlStreamingLimits Limits(int depth = 100, long input = 1_000_000, long capture = 1_000_000) =>
        new(
            maximumBufferedTokenBytes: 1_000_000,
            maximumNestingDepth: depth,
            maximumInputBytes: input,
            maximumQueryCaptureBytes: capture
        );

    private static HtmlStreamingLimitExceededException Capture(Action action)
    {
        try
        {
            action();
        }
        catch (HtmlStreamingLimitExceededException error)
        {
            return error;
        }
        throw new InvalidOperationException("Expected a streaming limit failure.");
    }

    private static async Task<HtmlStreamingLimitExceededException> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (HtmlStreamingLimitExceededException error)
        {
            return error;
        }
        throw new InvalidOperationException("Expected a streaming limit failure.");
    }

    private sealed class TestState;

    private sealed class NullSink : IUtf8HtmlTokenSink
    {
        public Utf8HtmlTokenCapture Capture => Utf8HtmlTokenCapture.Text;

        public void Text(ReadOnlySpan<byte> utf8) { }

        public Utf8HtmlStartTagCapture StartTag(Utf8HtmlName name) => Utf8HtmlStartTagCapture.Attributes;

        public bool WantsAttribute(Utf8HtmlName name) => true;

        public void Attribute(Utf8HtmlName name, ReadOnlySpan<byte> value) { }

        public void StartTagEnd(bool selfClosing) { }

        public void EndTag(Utf8HtmlName name) { }
    }
}
#endif
