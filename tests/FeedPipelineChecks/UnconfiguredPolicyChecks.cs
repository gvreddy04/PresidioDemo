using System.Xml.Linq;
using PresidioDemo.Feeds;

static class UnconfiguredPolicyChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
        Console.WriteLine("PASS " + message);
    }

    private static void Reject(Action action, string message)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            Console.WriteLine("PASS " + message);
            return;
        }
        throw new Exception("Expected policy rejection: " + message);
    }

    public static void Run(string root, string workspace)
    {
        string source = Path.Combine(root, "src", "config", "feed-policies.xml");
        var policies = FeedPolicyConfiguration.Load(source);
        var pending = new FeedPolicy(FeedType.PNR, [], configured: false);
        Check(policies[FeedType.PNR].IsConfigured && policies[FeedType.PNR].Fields.Count == 106,
            "PNR reference policy has only reviewed field paths");
        Check(policies.Where(pair => pair.Key != FeedType.PNR).All(pair => pair.Value.IsConfigured),
            "existing configured feeds stay enabled when the attribute is omitted");
        Reject(() => pending.Get("/pnr"), "unconfigured policy rejects field lookup");
        Reject(() => ConfiguredPiiIdentifier.CreatePlan([], pending),
            "unconfigured policy rejects even an empty protection plan");
        Reject(() => ConfiguredPiiIdentifier.CreatePlan([new("0", "/name", "Emily Carter")], pending),
            "unconfigured policy rejects parsed fields before calling Presidio");
        Reject(() => new FeedPolicy(FeedType.PNR, []), "configured policies still require fields");
        Reject(() => new FeedPolicy(FeedType.PNR, [new("/pnr", FieldIdentification.None, FieldAction.Keep)], configured: false),
            "unconfigured policies cannot carry dormant field rules");

        string invalid = Path.Combine(workspace, "unconfigured-policy.xml");
        var baseline = XDocument.Load(source);
        var pendingEntry = baseline.Root!.Elements("feed").Single(element => (string?)element.Attribute("type") == "PNR");
        pendingEntry.RemoveNodes();
        pendingEntry.SetAttributeValue("configured", "false");
        void Invalid(Action<XElement> mutate, string message)
        {
            var document = new XDocument(baseline);
            var pnr = document.Root!.Elements("feed").Single(element => (string?)element.Attribute("type") == "PNR");
            mutate(pnr);
            document.Save(invalid);
            Reject(() => FeedPolicyConfiguration.Load(invalid), message);
        }
        foreach (string value in new[] { "False", "0", "", "yes" })
        {
            Invalid(pnr => pnr.SetAttributeValue("configured", value), "invalid configured attribute is rejected: " + value);
        }
        Invalid(pnr => pnr.SetAttributeValue("configured", "true"), "enabled policy without rules is rejected");
        Invalid(pnr => pnr.Attribute("configured")!.Remove(), "omitting configured does not permit an empty policy");
        Invalid(pnr => pnr.Add(new XElement("field", new XAttribute("name", "/pnr"),
            new XAttribute("identification", "None"), new XAttribute("action", "Keep"))),
            "XML rejects fields on an unconfigured policy");

        var explicitlyConfigured = new XDocument(baseline);
        foreach (var feed in explicitlyConfigured.Root!.Elements("feed").Where(element => (string?)element.Attribute("type") != "PNR"))
        {
            feed.SetAttributeValue("configured", "true");
        }
        explicitlyConfigured.Save(invalid);
        Check(FeedPolicyConfiguration.Load(invalid)[FeedType.PnrLinking].IsConfigured,
            "explicit true keeps reviewed PNR-Linking policies valid");
    }
}
