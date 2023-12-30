using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AngleSharp.ReadOnlyDom.Benchmarks
{
    public sealed class UrlTests
    {
        readonly List<UrlTest> _tests;
        readonly bool _buffer;
        readonly string _extension;

        public UrlTests(string extension, bool withBuffer = true)
        {
            _tests = new List<UrlTest>();
            _buffer = withBuffer;
            _extension = extension;
        }

        public List<UrlTest> Tests => _tests;

        public async Task<UrlTests> Include(params string[] urls)
        {
            var tasks = new Task[urls.Length];

            for (int i = 0; i < urls.Length; i++)
            {
                tasks[i] = Include(urls[i]);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            return this;
        }

        public async Task<UrlTests> Include(string url)
        {
            var test = await UrlTest.For(url, _extension, _buffer).ConfigureAwait(false);
            _tests.Add(test);
            return this;
        }
    }
}
