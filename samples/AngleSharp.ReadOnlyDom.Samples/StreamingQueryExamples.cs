using AngleSharp.ReadOnlyDom.Streaming.Utf8Stream;

internal static class StreamingQueryExamples
{
    private static readonly byte[] Html =
        """
        <main>
          <article id="intro">
            <h1> A small <em>streaming</em> catalogue </h1>
            <p>Only requested values become owned output.</p>
          </article>
          <ul id="products">
            <li data-sku="A-1"><a href="/alpha"><h2> Alpha </h2></a><span class="price"> £10 </span></li>
            <li data-sku="B-2"><a href="/beta"><h2> Beta </h2></a><span class="price"> £20 </span></li>
          </ul>
          <footer><a href="/about">About</a><a href="https://example.com/help">External help</a></footer>
        </main>
        """u8.ToArray();

    internal static void Run()
    {
        Heading("UTF-8 STREAM QUERY — completed elements folded into arbitrary state");
        TypedRows();
        SubtreeText();
        Aggregate();
    }

    private static void TypedRows()
    {
        var products = StreamQuery.For<ProductFold>("ul").Id("products");
        var product = products
            .Child("li")
            .Attribute("data-sku")
            .OnClose(
                static (ref state, in element) =>
                {
                    state.Products.Add(
                        new Product(element.AttributeOrEmpty("data-sku"), state.Name, state.Price, state.Url)
                    );
                    state.Name = state.Price = state.Url = string.Empty;
                }
            );

        product
            .Descendant("a")
            .Attribute("href")
            .OnClose(static (ref state, in element) => state.Url = element.AttributeOrEmpty("href"));
        product.Descendant("h2").OnNormalizedText(static (ref state, in element) => state.Name = element.Text);
        product
            .Descendant("span")
            .Class("price")
            .OnNormalizedText(static (ref state, in element) => state.Price = element.Text);

        var result = products.Compile().Execute(Html, new ProductFold());
        Console.WriteLine($"typed rows       : {string.Join(", ", result.Products)}");
    }

    private static void SubtreeText()
    {
        var article = StreamQuery
            .For<List<string>>("article")
            .Id("intro")
            .OnNormalizedText(static (ref output, in element) => output.Add(element.Text));

        var output = article.Compile().Execute(Html, []);
        Console.WriteLine($"subtree text     : {output[0]}");
    }

    private static void Aggregate()
    {
        var page = StreamQuery.For<PageSummary>("main");
        page.Descendant("a")
            .Attribute("href")
            .OnNormalizedText(
                static (ref summary, in element) =>
                {
                    summary.Links++;
                    if (element.AttributeOrEmpty("href").StartsWith("http", StringComparison.Ordinal))
                        summary.ExternalLinks++;
                    summary.LinkTextCharacters += element.Text.Length;
                }
            );
        page.Descendant("p")
            .OnNormalizedText(
                static (ref summary, in element) =>
                    summary.ParagraphWords += element.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length
            );

        var summary = page.Compile().Execute(Html, new PageSummary());
        Console.WriteLine(
            $"aggregate        : {summary.Links} links ({summary.ExternalLinks} external), "
                + $"{summary.LinkTextCharacters} link-text chars, {summary.ParagraphWords} paragraph words"
        );
    }

    private static void Heading(string title)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine(new string('-', title.Length));
    }

    private sealed class ProductFold
    {
        internal List<Product> Products { get; } = [];
        internal string Name = string.Empty;
        internal string Price = string.Empty;
        internal string Url = string.Empty;
    }

    private sealed class PageSummary
    {
        internal int Links;
        internal int ExternalLinks;
        internal int LinkTextCharacters;
        internal int ParagraphWords;
    }

    private sealed record Product(string Sku, string Name, string Price, string Url);
}
