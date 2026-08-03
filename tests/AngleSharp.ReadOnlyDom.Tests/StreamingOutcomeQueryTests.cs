#if NET10_0
using System.IO.Pipelines;
using System.Text;
using AngleSharp.ReadOnlyDom.Streaming;
using AngleSharp.ReadOnlyDom.Streaming.Query;

namespace AngleSharp.Readonly.Tests;

public sealed class StreamingOutcomeQueryTests
{
    private const string Html = """
        <main id="tracking-results">
          <aside id="service-status" data-code="DEGRADED">Updates may be delayed.</aside>
          <template><div data-code="INVALID_TRACKING_NUMBER">stale template</div></template>

          <article class="shipment" data-id="GOOD">
            <table class="events"><tbody>
              <tr><td>2026-07-14</td><td>London</td><td>Delivered</td></tr>
              <tr><td>2026-07-13</td><td>Coventry</td><td>In transit</td></tr>
            </tbody></table>
          </article>

          <article class="shipment" data-id="EMPTY">
            <div data-code="NO_SCANS">Registered, but no scans yet.</div>
            <table class="events"><tbody></tbody></table>
          </article>

          <article class="shipment" data-id="BAD">
            <section data-code="INVALID_TRACKING_NUMBER">Invalid number.</section>
          </article>

          <article class="shipment" data-id="CURSED">
            <table class="events"><tbody></tbody></table>
            <div data-code="NO_DATA">No data.</div>
            <div data-code="BOT_CHALLENGE">Verify that you are human.</div>
          </article>

          <article class="shipment" data-id="BROKEN">
            <table class="events"><tbody>
              <tr><td></td><td></td><td></td></tr>
              <tr><td>not a date</td><td></td><td></td></tr>
            </tbody></table>
          </article>

          <article class="shipment" data-id="UNKNOWN"></article>
        </main>

        <table class="events"><tbody><tr><td>unrelated</td><td>table</td><td>Delivered</td></tr></tbody></table>
        """;

    [Test]
    public async Task QueryForestObservesCompetingOutcomesAndResolvesAfterEof()
    {
        var outcomes = CreateParser().Execute(Encoding.UTF8.GetBytes(Html), new ObservationState()).Resolve();

        await Assert
            .That(outcomes.Select(static item => item.Kind))
            .IsEquivalentTo([
                OutcomeKind.Success,
                OutcomeKind.PendingWithoutScans,
                OutcomeKind.InvalidTrackingNumber,
                OutcomeKind.BotChallenge,
                OutcomeKind.MalformedRows,
                OutcomeKind.Unrecognized,
            ]);
        await Assert.That(outcomes[0].ValidRows).IsEqualTo(2);
        await Assert.That(outcomes[1].TablePresent).IsTrue();
        await Assert.That(outcomes[1].RawRows).IsEqualTo(0);
        await Assert.That(outcomes[3].Evidence).IsEquivalentTo(["NO_DATA", "BOT_CHALLENGE"]);
        await Assert.That(outcomes.All(static item => item.ProviderDegraded)).IsTrue();
    }

    [Test]
    public async Task SegmentedPipeProducesTheSameResolvedOutcomes()
    {
        var bytes = Encoding.UTF8.GetBytes(Html);
        var pipe = new Pipe(new PipeOptions(minimumSegmentSize: 7, useSynchronizationContext: false));
        var execution = CreateParser().ExecuteAsync(pipe.Reader, new ObservationState()).AsTask();

        for (var offset = 0; offset < bytes.Length; offset += 7)
            await pipe.Writer.WriteAsync(bytes.AsMemory(offset, Math.Min(7, bytes.Length - offset)));
        await pipe.Writer.CompleteAsync();

        var outcomes = (await execution).Resolve();
        await pipe.Reader.CompleteAsync();

        await Assert
            .That(outcomes.Select(static item => item.Kind))
            .IsEquivalentTo([
                OutcomeKind.Success,
                OutcomeKind.PendingWithoutScans,
                OutcomeKind.InvalidTrackingNumber,
                OutcomeKind.BotChallenge,
                OutcomeKind.MalformedRows,
                OutcomeKind.Unrecognized,
            ]);
    }

    [Test]
    public async Task ApplicationResolutionDoesNotRunWhenStreamingLimitRejectsInput()
    {
        var resolved = false;
        var parser = StreamQuery.Observe(StreamQuery.For<ObservationState>("main"));
        var limits = new HtmlStreamingLimits(
            maximumBufferedTokenBytes: 64,
            maximumNestingDepth: 64,
            maximumInputBytes: 8,
            maximumQueryCaptureBytes: 1024
        );

        var rejected = false;
        try
        {
            var state = parser.Execute("<main>too long</main>"u8, new ObservationState(), limits);
            resolved = true;
            state.Resolve();
        }
        catch (HtmlStreamingLimitExceededException)
        {
            rejected = true;
        }

        await Assert.That(rejected).IsTrue();
        await Assert.That(resolved).IsFalse();
    }

    [Test]
    public async Task EofClosesMalformedScopeBeforeApplicationResolution()
    {
        var shipment = StreamQuery
            .For<ObservationState>("article")
            .Attribute("data-id")
            .OnStart(
                static (ref state, in element) =>
                    state.Begin(Encoding.UTF8.GetString(RequiredAttribute(element, "data-id")))
            )
            .OnEnd(static (ref state) => state.End());
        var parser = StreamQuery.Observe(shipment);

        var outcomes = parser.Execute("<article data-id=UNCLOSED>partial"u8, new ObservationState()).Resolve();

        await Assert.That(outcomes.Count).IsEqualTo(1);
        await Assert.That(outcomes[0].Id).IsEqualTo("UNCLOSED");
    }

    [Test]
    public async Task ObserveNormalizesDescendantsAndDeduplicatesTheirSharedRoot()
    {
        var starts = 0;
        var root = StreamQuery.For<ObservationState>("main");
        var descendant = root.Descendant("article").OnStart((ref ObservationState _, in Element _) => starts++);

        StreamQuery.Observe(root, descendant).Execute("<main><article></article></main>"u8, new ObservationState());

        await Assert.That(starts).IsEqualTo(1);
    }

    [Test]
    public async Task QueryNodeLimitAppliesAcrossTheWholeObservationSet()
    {
        var queries = Enumerable.Range(0, 65).Select(static _ => StreamQuery.For<ObservationState>("div")).ToArray();

        var rejected = false;
        try
        {
            StreamQuery.Observe(queries);
        }
        catch (NotSupportedException)
        {
            rejected = true;
        }

        await Assert.That(rejected).IsTrue();
    }

    private static QueryPlan<ObservationState> CreateParser()
    {
        var shipments = StreamQuery
            .For<ObservationState>("article")
            .Class("shipment")
            .Attribute("data-id")
            .OnStart(
                static (ref state, in element) =>
                    state.Begin(Encoding.UTF8.GetString(RequiredAttribute(element, "data-id")))
            )
            .OnEnd(static (ref state) => state.End());

        var table = shipments
            .Descendant("table")
            .Class("events")
            .OnStart(static (ref ObservationState state, in Element _) => state.Current.TablePresent = true);
        var row = table
            .Descendant("tbody")
            .Child("tr")
            .OnStart(static (ref ObservationState state, in Element _) => state.Current.BeginRow())
            .OnEnd(static (ref state) => state.Current.EndRow());
        row.Child("td").OnNormalizedText(static (ref state, in element) => state.Current.Cells.Add(element.GetText()));

        AddEvidenceQuery(shipments, "div");
        AddEvidenceQuery(shipments, "section");

        var providerStatus = StreamQuery
            .For<ObservationState>("aside")
            .Id("service-status")
            .Attribute("data-code")
            .OnClose(
                static (ref state, in element) =>
                    state.ProviderDegraded = element.GetAttributeOrEmpty("data-code") == "DEGRADED"
            );

        return StreamQuery.Observe(shipments, providerStatus);
    }

    private static void AddEvidenceQuery(QueryNode<ObservationState> article, string tag)
    {
        article
            .Descendant(tag)
            .Attribute("data-code")
            .OnNormalizedText(
                static (ref state, in element) => state.Current.Evidence.Add(element.GetAttributeOrEmpty("data-code"))
            );
    }

    private static ReadOnlySpan<byte> RequiredAttribute(in Element element, string name) =>
        element.TryGetAttribute(name, out var value)
            ? value
            : throw new InvalidOperationException($"Missing '{name}'.");

    private sealed class ObservationState
    {
        private readonly List<ArticleObservation> _articles = [];

        internal ArticleObservation Current { get; private set; } = null!;
        internal bool ProviderDegraded { get; set; }

        internal void Begin(string id) => Current = new ArticleObservation(id);

        internal void End()
        {
            _articles.Add(Current);
            Current = null!;
        }

        internal IReadOnlyList<ShipmentOutcome> Resolve() =>
            _articles.Select(article => article.Resolve(ProviderDegraded)).ToArray();
    }

    private sealed class ArticleObservation(string id)
    {
        internal string Id { get; } = id;
        internal bool TablePresent { get; set; }
        internal int RawRows { get; private set; }
        internal int ValidRows { get; private set; }
        internal List<string> Cells { get; } = [];
        internal List<string> Evidence { get; } = [];

        internal void BeginRow() => Cells.Clear();

        internal void EndRow()
        {
            RawRows++;
            if (Cells.Count >= 3 && Cells[2].Length != 0)
                ValidRows++;
        }

        internal ShipmentOutcome Resolve(bool providerDegraded)
        {
            var kind =
                Evidence.Contains("BOT_CHALLENGE", StringComparer.Ordinal) ? OutcomeKind.BotChallenge
                : Evidence.Contains("INVALID_TRACKING_NUMBER", StringComparer.Ordinal)
                    ? OutcomeKind.InvalidTrackingNumber
                : ValidRows != 0 ? OutcomeKind.Success
                : TablePresent && RawRows == 0 && Evidence.Contains("NO_SCANS", StringComparer.Ordinal)
                    ? OutcomeKind.PendingWithoutScans
                : TablePresent && RawRows != 0 ? OutcomeKind.MalformedRows
                : OutcomeKind.Unrecognized;
            return new ShipmentOutcome(Id, kind, TablePresent, RawRows, ValidRows, [.. Evidence], providerDegraded);
        }
    }

    private sealed record ShipmentOutcome(
        string Id,
        OutcomeKind Kind,
        bool TablePresent,
        int RawRows,
        int ValidRows,
        IReadOnlyList<string> Evidence,
        bool ProviderDegraded
    );

    private enum OutcomeKind
    {
        Success,
        PendingWithoutScans,
        InvalidTrackingNumber,
        BotChallenge,
        MalformedRows,
        Unrecognized,
    }
}
#endif
