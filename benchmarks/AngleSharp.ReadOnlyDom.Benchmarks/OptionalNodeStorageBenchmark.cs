#if NET10_0
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

[MemoryDiagnoser]
public class OptionalNodeStorageLookupBenchmark
{
    private const int NodeCount = 16_384;
    private int[] _denseValues = null!;
    private bool[] _densePresent = null!;
    private int[] _sparseHandles = null!;
    private int[] _sparseValues = null!;

    [Params(1, 10, 50, 90)]
    public int DensityPercent { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var handles = new List<int>();
        var values = new List<int>();
        _denseValues = new int[NodeCount];
        _densePresent = new bool[NodeCount];
        for (var handle = 0; handle < NodeCount; handle++)
        {
            if (handle % 100 >= DensityPercent)
                continue;
            var value = handle * 17 + 3;
            handles.Add(handle);
            values.Add(value);
            _denseValues[handle] = value;
            _densePresent[handle] = true;
        }
        _sparseHandles = handles.ToArray();
        _sparseValues = values.ToArray();
    }

    [Benchmark(Baseline = true)]
    public int DenseAllNodeLookup()
    {
        var checksum = 0;
        for (var handle = 0; handle < NodeCount; handle++)
            if (_densePresent[handle])
                checksum += _denseValues[handle];
        return checksum;
    }

    [Benchmark]
    public int SparseBinaryAllNodeLookup()
    {
        var checksum = 0;
        for (var handle = 0; handle < NodeCount; handle++)
        {
            var index = Array.BinarySearch(_sparseHandles, handle);
            if (index >= 0)
                checksum += _sparseValues[index];
        }
        return checksum;
    }

    [Benchmark]
    public int SparseForwardScan()
    {
        var checksum = 0;
        for (var index = 0; index < _sparseValues.Length; index++)
            checksum += _sparseValues[index];
        return checksum;
    }
}
#endif
