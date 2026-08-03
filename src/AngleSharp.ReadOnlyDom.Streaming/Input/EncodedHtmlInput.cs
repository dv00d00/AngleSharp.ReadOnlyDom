using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using AngleSharp.ReadOnlyDom.Streaming.Tokenization;

namespace AngleSharp.ReadOnlyDom.Streaming.Input;

internal static class EncodedHtmlInput
{
    internal static async ValueTask<Utf8HtmlTokenizerCounters> TokenizeAsync(
        PipeReader reader,
        HtmlInputEncoding inputEncoding,
        IUtf8HtmlTokenSink sink,
        CancellationToken cancellationToken,
        int inputSliceSize = int.MaxValue,
        Func<ValueTask>? afterInputSlice = null,
        HtmlStreamingLimits? limits = null
    )
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputSliceSize);

        if (!inputEncoding.Detect && inputEncoding.Encoding is null)
            throw new ArgumentException(
                "Use HtmlInputEncoding.Known or HtmlInputEncoding.Auto.",
                nameof(inputEncoding)
            );

        limits ??= HtmlStreamingLimits.Default;
        if (inputEncoding.Detect)
        {
            return await TokenizeDetectedAsync(
                    reader,
                    sink,
                    inputEncoding.Fallback,
                    cancellationToken,
                    inputSliceSize,
                    afterInputSlice,
                    limits
                )
                .ConfigureAwait(false);
        }

        var sourceEncoding = inputEncoding.Encoding!;
        var tokenizer = new Utf8HtmlTokenizer(sink, stateMetrics: null, limits, countInputBytes: false);
        if (sourceEncoding.CodePage == Encoding.UTF8.CodePage)
        {
            var input = new Utf8HtmlTokenizerInput(tokenizer, limits: limits);
            await PumpUtf8Async(
                    reader,
                    input,
                    cancellationToken,
                    inputSliceSize,
                    afterInputSlice,
                    inputBytesConsumed: 0,
                    limits.MaximumInputBytes
                )
                .ConfigureAwait(false);
            input.Complete();
            return input.Counters;
        }

        using var transcoder = new Transcoder(sourceEncoding, tokenizer);
        await PumpEncodedAsync(
                reader,
                transcoder,
                cancellationToken,
                inputSliceSize,
                afterInputSlice,
                inputBytesConsumed: 0,
                limits.MaximumInputBytes
            )
            .ConfigureAwait(false);
        transcoder.Complete();

        tokenizer.Complete();
        return tokenizer.Counters;
    }

    private static async ValueTask<Utf8HtmlTokenizerCounters> TokenizeDetectedAsync(
        PipeReader reader,
        IUtf8HtmlTokenSink sink,
        Encoding? fallback,
        CancellationToken cancellationToken,
        int inputSliceSize,
        Func<ValueTask>? afterInputSlice,
        HtmlStreamingLimits limits
    )
    {
        var prefix = ArrayPool<byte>.Shared.Rent(HtmlEncodingSniffer.PrefixSize);
        try
        {
            var count = await ReadPrefixAsync(reader, prefix, cancellationToken, limits.MaximumInputBytes)
                .ConfigureAwait(false);
            var detection = HtmlEncodingSniffer.Detect(prefix.AsSpan(0, count), fallback);
            var tokenizer = new Utf8HtmlTokenizer(sink, stateMetrics: null, limits, countInputBytes: false);

            if (detection.Encoding.CodePage == Encoding.UTF8.CodePage)
            {
                var input = new Utf8HtmlTokenizerInput(tokenizer, limits: limits);
                input.Write(prefix.AsSpan(detection.PreambleLength, count - detection.PreambleLength));
                if (afterInputSlice is not null)
                    await afterInputSlice().ConfigureAwait(false);
                await PumpUtf8Async(
                        reader,
                        input,
                        cancellationToken,
                        inputSliceSize,
                        afterInputSlice,
                        count,
                        limits.MaximumInputBytes
                    )
                    .ConfigureAwait(false);
                input.Complete();
                return input.Counters;
            }

            using var transcoder = new Transcoder(detection.Encoding, tokenizer);
            transcoder.Write(prefix.AsSpan(detection.PreambleLength, count - detection.PreambleLength));
            if (afterInputSlice is not null)
                await afterInputSlice().ConfigureAwait(false);
            await PumpEncodedAsync(
                    reader,
                    transcoder,
                    cancellationToken,
                    inputSliceSize,
                    afterInputSlice,
                    count,
                    limits.MaximumInputBytes
                )
                .ConfigureAwait(false);
            transcoder.Complete();
            tokenizer.Complete();
            return tokenizer.Counters;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(prefix);
        }
    }

    private static async ValueTask<int> ReadPrefixAsync(
        PipeReader reader,
        byte[] destination,
        CancellationToken cancellationToken,
        long maximumInputBytes
    )
    {
        var written = 0;
        while (written < HtmlEncodingSniffer.PrefixSize)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;
            if (result.IsCanceled)
            {
                reader.AdvanceTo(buffer.Start, buffer.End);
                throw new OperationCanceledException(cancellationToken);
            }

            var length = (int)Math.Min(buffer.Length, HtmlEncodingSniffer.PrefixSize - written);
            if (length != 0)
            {
                var advanced = false;
                try
                {
                    EnsureInputBudget(written, length, maximumInputBytes);
                    buffer.Slice(0, length).CopyTo(destination.AsSpan(written));
                    var consumed = buffer.GetPosition(length);
                    reader.AdvanceTo(consumed, consumed);
                    advanced = true;
                    written += length;
                }
                catch
                {
                    if (!advanced)
                        reader.AdvanceTo(buffer.End);
                    throw;
                }
            }
            else
            {
                reader.AdvanceTo(buffer.Start, buffer.End);
            }

            if (result.IsCompleted)
                break;
        }

        return written;
    }

    private static async ValueTask PumpUtf8Async(
        PipeReader reader,
        Utf8HtmlTokenizerInput input,
        CancellationToken cancellationToken,
        int inputSliceSize,
        Func<ValueTask>? afterInputSlice,
        long inputBytesConsumed,
        long maximumInputBytes
    )
    {
        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;
            if (result.IsCanceled)
            {
                reader.AdvanceTo(buffer.Start, buffer.End);
                throw new OperationCanceledException(cancellationToken);
            }

            try
            {
                foreach (var segment in buffer)
                {
                    for (var offset = 0; offset < segment.Length; offset += inputSliceSize)
                    {
                        var length = Math.Min(inputSliceSize, segment.Length - offset);
                        inputBytesConsumed = EnsureInputBudget(inputBytesConsumed, length, maximumInputBytes);
                        input.Write(segment.Span.Slice(offset, length));
                        if (afterInputSlice is not null)
                            await afterInputSlice().ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                reader.AdvanceTo(buffer.End);
            }

            if (result.IsCompleted)
                return;
        }
    }

    private static async ValueTask PumpEncodedAsync(
        PipeReader reader,
        Transcoder transcoder,
        CancellationToken cancellationToken,
        int inputSliceSize,
        Func<ValueTask>? afterInputSlice,
        long inputBytesConsumed,
        long maximumInputBytes
    )
    {
        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;
            if (result.IsCanceled)
            {
                reader.AdvanceTo(buffer.Start, buffer.End);
                throw new OperationCanceledException(cancellationToken);
            }

            try
            {
                foreach (var segment in buffer)
                {
                    for (var offset = 0; offset < segment.Length; offset += inputSliceSize)
                    {
                        var length = Math.Min(inputSliceSize, segment.Length - offset);
                        inputBytesConsumed = EnsureInputBudget(inputBytesConsumed, length, maximumInputBytes);
                        transcoder.Write(segment.Span.Slice(offset, length));
                        if (afterInputSlice is not null)
                            await afterInputSlice().ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                reader.AdvanceTo(buffer.End);
            }

            if (result.IsCompleted)
                return;
        }
    }

    private static long EnsureInputBudget(long consumed, int additional, long maximum)
    {
        var observed = consumed > long.MaxValue - additional ? long.MaxValue : consumed + additional;
        if (observed > maximum)
            throw new HtmlStreamingLimitExceededException(HtmlStreamingLimit.InputBytes, maximum, observed);
        return observed;
    }

    private sealed class Transcoder : IDisposable
    {
        private const int CharacterBufferSize = 4 * 1024;

        private readonly Decoder _decoder;
        private readonly Encoder _encoder;
        private readonly Utf8HtmlTokenizer _tokenizer;
        private readonly char[] _characters;
        private readonly byte[] _utf8;

        internal Transcoder(Encoding sourceEncoding, Utf8HtmlTokenizer tokenizer)
        {
            _decoder = sourceEncoding.GetDecoder();
            _encoder = Encoding.UTF8.GetEncoder();
            _tokenizer = tokenizer;
            _characters = ArrayPool<char>.Shared.Rent(CharacterBufferSize);
            _utf8 = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(CharacterBufferSize));
        }

        internal void Write(ReadOnlySpan<byte> source)
        {
            while (!source.IsEmpty)
            {
                _decoder.Convert(source, _characters, flush: false, out var bytesUsed, out var charactersUsed, out _);
                if (bytesUsed == 0 && charactersUsed == 0)
                    throw new InvalidOperationException("The source decoder made no progress.");
                source = source[bytesUsed..];
                Encode(_characters.AsSpan(0, charactersUsed), flush: false);
            }
        }

        internal void Complete()
        {
            var decoderCompleted = false;
            while (!decoderCompleted)
            {
                _decoder.Convert(
                    ReadOnlySpan<byte>.Empty,
                    _characters,
                    flush: true,
                    out _,
                    out var charactersUsed,
                    out decoderCompleted
                );
                Encode(_characters.AsSpan(0, charactersUsed), flush: false);
            }

            Encode(ReadOnlySpan<char>.Empty, flush: true);
        }

        private void Encode(ReadOnlySpan<char> characters, bool flush)
        {
            var completed = false;
            do
            {
                _encoder.Convert(characters, _utf8, flush, out var charactersUsed, out var bytesUsed, out completed);
                if (!completed && charactersUsed == 0 && bytesUsed == 0)
                    throw new InvalidOperationException("The UTF-8 encoder made no progress.");
                characters = characters[charactersUsed..];
                if (bytesUsed != 0)
                    _tokenizer.Write(_utf8.AsSpan(0, bytesUsed));
            } while (!completed);
        }

        public void Dispose()
        {
            ArrayPool<char>.Shared.Return(_characters);
            ArrayPool<byte>.Shared.Return(_utf8);
        }
    }
}
