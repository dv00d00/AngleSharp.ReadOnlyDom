using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AngleSharp.ReadOnlyDom.Benchmarks
{
    public sealed class UrlTests(string extension, bool withBuffer = true)
    {
        readonly List<UrlTest> _tests = new();

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
            var test = await UrlTest.For(url, extension, withBuffer).ConfigureAwait(false);
            _tests.Add(test);
            return this;
        }
    }
}