using System.Diagnostics;
using System.Globalization;

namespace PresidioDemo.Feeds;

public interface IFeedProtector
{
    ValueTask<IReadOnlyDictionary<string, string>> ProtectAsync(IReadOnlyList<FeedField> fields, FeedPolicy policy, CancellationToken cancellationToken);
}

/// <summary>Local Presidio applies the selected feed policy; kept fields bypass Python.</summary>
public sealed class PresidioFeedProtector : IFeedProtector
{
    // The shared Python engines are reused safely; release between fields for other feeds.
    private static readonly SemaphoreSlim EngineGate = new(1, 1);
    public async ValueTask<IReadOnlyDictionary<string, string>> ProtectAsync(IReadOnlyList<FeedField> fields, FeedPolicy policy, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var plan = new List<(FeedField Field, FieldPolicy Rule)>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        // Validate the whole plan before applying any action.
        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.Id) || string.IsNullOrWhiteSpace(field.Name) || field.Value is null || !ids.Add(field.Id))
                throw new InvalidDataException("The binary codec returned invalid or duplicate fields.");
            plan.Add((field, policy.Get(field.Name)));
        }
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (field, rule) in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string value = field.Value;
            if (rule.Action != FieldAction.Keep && value.Length > 0)
            {
                await EngineGate.WaitAsync(cancellationToken);
                try
                {
                    value = PythonHost.Call("protect_configured_field", field.Value, field.Name,
                        rule.Identification.ToString(), rule.Action.ToString(), rule.Entity, rule.Replacement,
                        rule.Entities, rule.Threshold.ToString(CultureInfo.InvariantCulture), rule.MaskCharacter,
                        rule.MaskCharacters.ToString(CultureInfo.InvariantCulture), rule.FromEnd ? "true" : "false");
                }
                catch (Exception) { throw new InvalidDataException("Configured local Presidio processing failed; input retained and no output was published."); }
                finally { EngineGate.Release(); }
            }
            if (FieldPolicy.IsPnr(field.Name) && value != field.Value)
                throw new InvalidDataException("Protection changed a PNR.");
            if (rule.Identification == FieldIdentification.Known && field.Value.Length > 0 && value == field.Value)
                throw new InvalidDataException("A configured personal field was not changed by its protection action.");
            result.Add(field.Id, value);
        }
        Console.WriteLine($"{FeedCatalog.Get(policy.Feed).FolderName,-12} FIELDS     {fields.Count} total | {plan.Count(p => p.Rule.Action == FieldAction.Keep)} kept | {plan.Count(p => p.Rule.Identification == FieldIdentification.Known)} known | {plan.Count(p => p.Rule.Identification == FieldIdentification.Analyze)} analyzed | {timer.ElapsedMilliseconds} ms (includes engine wait)");
        return result;
    }
}