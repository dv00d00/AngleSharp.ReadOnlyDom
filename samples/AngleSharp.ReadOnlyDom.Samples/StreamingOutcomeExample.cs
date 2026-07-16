using System.Text;
using AngleSharp.ReadOnlyDom.Streaming.Utf8Stream.Query;

internal static class StreamingOutcomeExample
{
    private static readonly byte[] Html =
        """
        <main id="tracking-results">
          <aside id="service-status" data-code="DEGRADED">Tracking updates may be delayed.</aside>

          <template>
            <div data-code="INVALID_TRACKING_NUMBER">A stale client-side template.</div>
          </template>

          <article class="shipment" data-tracking-id="GOOD-001">
            <table class="events"><tbody>
              <tr><td>2026-07-14 09:12</td><td>London</td><td>Out for delivery</td></tr>
              <tr><td>2026-07-13 21:40</td><td>Coventry</td><td>In transit</td></tr>
            </tbody></table>
          </article>

          <article class="shipment" data-tracking-id="EMPTY-002">
            <div data-code="NO_SCANS">Registered, but no scans are available yet.</div>
            <table class="events"><tbody></tbody></table>
          </article>

          <article class="shipment" data-tracking-id="BAD-003">
            <section data-code="INVALID_TRACKING_NUMBER">The supplied number is invalid.</section>
          </article>

          <article class="shipment" data-tracking-id="CURSED-004">
            <table class="events"><tbody></tbody></table>
            <div data-code="NO_DATA">No tracking information was found.</div>
            <div data-code="BOT_CHALLENGE">Please verify that you are human.</div>
          </article>

          <article class="shipment" data-tracking-id="BROKEN-005">
            <table class="events"><tbody>
              <tr><td></td><td></td><td></td></tr>
              <tr><td>not a date</td><td></td><td></td></tr>
            </tbody></table>
          </article>

          <article class="shipment" data-tracking-id="UNKNOWN-006"></article>
        </main>

        <table class="events"><tbody>
          <tr><td>Marketing</td><td>Not tracking data</td><td>Delivered</td></tr>
        </tbody></table>
        """u8.ToArray();

    internal static void Run()
    {
        Heading("STREAM OBSERVATIONS — competing interpretations resolved after EOF");

        var shipments = StreamQuery
            .For<TrackingEvidence>("article")
            .Class("shipment")
            .Attribute("data-tracking-id")
            .OnStart(
                static (ref evidence, in element) =>
                    evidence.BeginShipment(Encoding.UTF8.GetString(Required(element, "data-tracking-id")))
            )
            .OnEnd(static (ref evidence) => evidence.EndShipment());

        var row = shipments
            .Descendant("table")
            .Class("events")
            .OnStart(static (ref TrackingEvidence evidence, in Element _) => evidence.Current.TablePresent = true)
            .Descendant("tbody")
            .Child("tr")
            .OnStart(static (ref TrackingEvidence evidence, in Element _) => evidence.Current.BeginRow())
            .OnEnd(static (ref evidence) => evidence.Current.EndRow());
        row.Child("td")
            .OnNormalizedText(static (ref evidence, in cell) => evidence.Current.Cells.Add(cell.GetText()));

        ObserveCode(shipments, "div");
        ObserveCode(shipments, "section");

        var providerStatus = StreamQuery
            .For<TrackingEvidence>("aside")
            .Id("service-status")
            .Attribute("data-code")
            .OnClose(
                static (ref evidence, in status) =>
                    evidence.ProviderDegraded = status.GetAttributeOrEmpty("data-code") == "DEGRADED"
            );

        var parser = StreamQuery
            .Observe(shipments, providerStatus)
            .Resolve(static evidence => evidence.Resolve());

        foreach (var outcome in parser.Execute(Html, new TrackingEvidence()))
        {
            Console.WriteLine(
                $"{outcome.TrackingId,-11}: {outcome.Kind,-24} "
                    + $"table={outcome.TablePresent,-5} rows={outcome.RawRows}/{outcome.ValidRows} "
                    + $"evidence=[{string.Join(", ", outcome.Evidence)}]"
            );
        }
    }

    private static void ObserveCode(QueryNode<TrackingEvidence> shipment, string tag) =>
        shipment
            .Descendant(tag)
            .Attribute("data-code")
            .OnNormalizedText(
                static (ref evidence, in message) =>
                    evidence.Current.Evidence.Add(message.GetAttributeOrEmpty("data-code"))
            );

    private static ReadOnlySpan<byte> Required(in Element element, string name) =>
        element.TryGetAttribute(name, out var value)
            ? value
            : throw new InvalidOperationException($"Missing required attribute '{name}'.");

    private static void Heading(string title)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine(new string('-', title.Length));
    }

    private sealed class TrackingEvidence
    {
        private readonly List<ShipmentEvidence> _shipments = [];

        internal ShipmentEvidence Current { get; private set; } = null!;
        internal bool ProviderDegraded { get; set; }

        internal void BeginShipment(string trackingId) => Current = new ShipmentEvidence(trackingId);

        internal void EndShipment()
        {
            _shipments.Add(Current);
            Current = null!;
        }

        internal IReadOnlyList<ShipmentOutcome> Resolve() =>
            _shipments.Select(shipment => shipment.Resolve(ProviderDegraded)).ToArray();
    }

    private sealed class ShipmentEvidence(string trackingId)
    {
        internal string TrackingId { get; } = trackingId;
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
            var kind = Evidence.Contains("BOT_CHALLENGE", StringComparer.Ordinal)
                ? ShipmentOutcomeKind.BotChallenge
                : Evidence.Contains("INVALID_TRACKING_NUMBER", StringComparer.Ordinal)
                    ? ShipmentOutcomeKind.InvalidTrackingNumber
                    : ValidRows != 0
                        ? ShipmentOutcomeKind.Success
                        : TablePresent && RawRows == 0 && Evidence.Contains("NO_SCANS", StringComparer.Ordinal)
                            ? ShipmentOutcomeKind.PendingWithoutScans
                            : TablePresent && RawRows != 0
                                ? ShipmentOutcomeKind.MalformedRows
                                : ShipmentOutcomeKind.Unrecognized;

            return new ShipmentOutcome(
                TrackingId,
                kind,
                TablePresent,
                RawRows,
                ValidRows,
                [.. Evidence],
                providerDegraded
            );
        }
    }

    private sealed record ShipmentOutcome(
        string TrackingId,
        ShipmentOutcomeKind Kind,
        bool TablePresent,
        int RawRows,
        int ValidRows,
        IReadOnlyList<string> Evidence,
        bool ProviderDegraded
    );

    private enum ShipmentOutcomeKind
    {
        Success,
        PendingWithoutScans,
        InvalidTrackingNumber,
        BotChallenge,
        MalformedRows,
        Unrecognized,
    }
}
