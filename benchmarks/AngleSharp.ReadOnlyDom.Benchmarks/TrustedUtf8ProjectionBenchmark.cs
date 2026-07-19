#if NET10_0

using System.Buffers;
using System.Text;
using AngleSharp.ReadOnlyDom.Streaming;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

[MemoryDiagnoser]
public class TrustedUtf8ProjectionBenchmark
{
    private byte[] _input = null!;

    [Params("Ascii", "Multilingual")]
    public string Payload { get; set; } = null!;

    [GlobalSetup]
    public void Setup()
    {
        var fragment = Payload == "Ascii"
            ? "  Alpha beta gamma delta\n"
            : "  Alpha\u00a0Ж\u2003東京 🙂 delta\n";
        _input = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(fragment, 1024)));

        var expected = RuneValidatedCore();
        var actual = TrustedCore();
        if (!actual.WrittenSpan.SequenceEqual(expected.WrittenSpan))
            throw new InvalidOperationException("Trusted UTF-8 projection changed normalized text.");
    }

    [Benchmark(Baseline = true)]
    public int RuneValidated() => RuneValidatedCore().WrittenCount;

    [Benchmark]
    public int Trusted() => TrustedCore().WrittenCount;

    private ArrayBufferWriter<byte> RuneValidatedCore()
    {
        var output = new ArrayBufferWriter<byte>(_input.Length);
        var writer = new RuneValidatedWriter(output);
        writer.Append(_input);
        return output;
    }

    private ArrayBufferWriter<byte> TrustedCore()
    {
        var output = new ArrayBufferWriter<byte>(_input.Length);
        var writer = new NormalizedUtf8Writer(output);
        writer.Append(_input);
        return output;
    }

    private sealed class RuneValidatedWriter(IBufferWriter<byte> output)
    {
        private bool _hasContent;
        private bool _pendingSpace;

        internal void Append(ReadOnlySpan<byte> utf8)
        {
            while (!utf8.IsEmpty)
            {
                var status = Rune.DecodeFromUtf8(utf8, out var rune, out var consumed);
                if (status != OperationStatus.Done)
                    throw new InvalidOperationException("The benchmark fixture is not well-formed UTF-8.");

                var scalar = utf8[..consumed];
                utf8 = utf8[consumed..];
                if (rune.Value is 0x09 or 0x0A or 0x0C or 0x0D or 0x20 or 0x00A0)
                {
                    _pendingSpace = _hasContent;
                    continue;
                }

                if (_pendingSpace)
                {
                    Write(" "u8);
                    _pendingSpace = false;
                }
                Write(scalar);
                _hasContent = true;
            }
        }

        private void Write(ReadOnlySpan<byte> value)
        {
            value.CopyTo(output.GetSpan(value.Length));
            output.Advance(value.Length);
        }
    }
}

#endif
