using System.Net.Http;
using System.Threading.Tasks;
using AngleSharp.ReadOnlyDom.Helpers;
using BenchmarkDotNet.Running;

namespace AngleSharp.ReadOnlyDom.Benchmarks
{
    static class Program
    {
        static async Task Main(string[] args)
        {
#if RELEASE
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args); 
            return;
#endif
            
            // var benchmark = new HttpParsingBenchmark();
            // benchmark.Setup();
            // benchmark.Id = "15895974334";
            // await benchmark.CustomLibLevel();

            var client = new HttpClient();
            using var jz = await client.DownloadChars(new HttpRequestMessage(HttpMethod.Get, "https://vk.com"));
        }
    }
}