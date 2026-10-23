using System.Diagnostics;
using System.Globalization;
using PresidioDemo.Feeds.Shared;

namespace PresidioDemo.Protection;

#region Interfaces

/// <summary>
/// Defines configured protection of a complete parsed field set.
/// </summary>
public interface IFeedProtector
{

    #region Methods

    /// <summary>
    /// Applies the reviewed field rules through local Presidio while preserving kept and empty values.
    /// </summary>
    /// <param name="fields">The complete ordered parsed field set or reviewed field definitions.</param>
    /// <param name="policy">The reviewed field rules for this feed route.</param>
    /// <param name="cancellationToken">The token used to interrupt processing while retaining recoverable input.</param>
    /// <returns>A task containing every protected or preserved value indexed by its original field identifier.</returns>
    ValueTask<IReadOnlyDictionary<string, string>> ProtectAsync(IReadOnlyList<FeedField> fields, FeedPolicy policy, CancellationToken cancellationToken);

    #endregion
}

#endregion

#region Classes

/// <summary>
/// Local Presidio applies the selected feed policy; kept fields bypass Python.
/// </summary>
public sealed class PresidioFeedProtector : IFeedProtector
{

    #region Fields and Constants

    // The shared Python engines are reused safely; release between fields for other feeds.
    /// <summary>
    /// Serializes calls into the shared embedded Presidio engines while allowing independent feed processing.
    /// </summary>
    private static readonly SemaphoreSlim EngineGate = new(1, 1);

    #endregion

    #region Methods

    /// <summary>
    /// Applies the reviewed field rules through local Presidio while preserving kept and empty values.
    /// </summary>
    /// <param name="fields">The complete ordered parsed field set or reviewed field definitions.</param>
    /// <param name="policy">The reviewed field rules for this feed route.</param>
    /// <param name="cancellationToken">The token used to interrupt processing while retaining recoverable input.</param>
    /// <returns>A task containing every protected or preserved value indexed by its original field identifier.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    public async ValueTask<IReadOnlyDictionary<string, string>> ProtectAsync(IReadOnlyList<FeedField> fields, FeedPolicy policy, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var plan = ConfiguredPiiIdentifier.CreatePlan(fields, policy);
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
                        rule.MaskCount(field.Value).ToString(CultureInfo.InvariantCulture), rule.FromEnd ? "true" : "false");
                }
                catch (Exception) { throw new InvalidDataException("Configured local Presidio processing failed; input retained and no output was published."); }
                finally { EngineGate.Release(); }
            }
            if (FieldPolicy.IsPnr(field.Name) && value != field.Value)
            {
                throw new InvalidDataException("Protection changed a PNR.");
            }

            if (rule.Identification == FieldIdentification.Known && field.Value.Length > 0 && value == field.Value && !rule.IsAlreadyMasked(field.Value))
            {
                throw new InvalidDataException("A configured personal field was not changed by its protection action.");
            }

            result.Add(field.Id, value);
        }
        Console.WriteLine($"{FeedCatalog.Get(policy.Feed).FolderName,-12} FIELDS     {fields.Count} total | {plan.Count(p => p.Rule.Action == FieldAction.Keep)} kept | {plan.Count(p => p.Rule.Identification == FieldIdentification.Known)} known | {plan.Count(p => p.Rule.Identification == FieldIdentification.Analyze)} analyzed | {timer.ElapsedMilliseconds} ms (includes engine wait)");
        return result;
    }

    #endregion
}

/// <summary>
/// Selects exact configured fields before any anonymization calls are made.
/// </summary>
public static class ConfiguredPiiIdentifier
{

    #region Methods

    /// <summary>
    /// Validates the complete field set and resolves each field to its configured protection rule.
    /// </summary>
    /// <param name="fields">The complete ordered parsed field set or reviewed field definitions.</param>
    /// <param name="policy">The reviewed field rules for this feed route.</param>
    /// <returns>The complete validated field-and-policy pairs in input order.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    public static IReadOnlyList<(FeedField Field, FieldPolicy Rule)> CreatePlan(IReadOnlyList<FeedField> fields, FeedPolicy policy)
    {
        policy.EnsureConfigured();
        var plan = new List<(FeedField Field, FieldPolicy Rule)>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.Id) || string.IsNullOrWhiteSpace(field.Name) || field.Value is null || !ids.Add(field.Id))
            {
                throw new InvalidDataException("The feed codec returned invalid or duplicate fields.");
            }

            plan.Add((field, policy.Get(field.Name)));
        }
        return plan.AsReadOnly();
    }

    #endregion
}

#endregion
