using PresidioDemo.Feeds.Shared;
using PresidioDemo.Protection;

namespace PresidioDemo.Pipeline;

/// <summary>
/// The three validated XML configurations that drive every feed workflow.
/// </summary>
/// <param name="Policies">The field policies for each feed.</param>
/// <param name="XdrSchemas">The structural XDR schemas for the binary feeds.</param>
/// <param name="ReferenceLayouts">The reference mappings for the binary feeds.</param>
public sealed record FeedPipelineConfiguration(IReadOnlyDictionary<FeedType, FeedPolicy> Policies,
    IReadOnlyDictionary<FeedType, XdrFeedSchema> XdrSchemas, IReadOnlyDictionary<FeedType, ReferenceFeedLayout> ReferenceLayouts)
{

    #region Methods

    /// <summary>
    /// Loads and cross-validates the policy, XDR layout and reference layout files before any input is read.
    /// </summary>
    /// <param name="policyFile">The feed-policies XML file.</param>
    /// <param name="xdrLayoutFile">The XDR layouts XML file.</param>
    /// <param name="referenceLayoutFile">The reference layouts XML file.</param>
    /// <returns>The validated configuration.</returns>
    /// <exception cref="InvalidDataException">A file is invalid or the files disagree.</exception>
    public static FeedPipelineConfiguration Load(string policyFile, string xdrLayoutFile, string referenceLayoutFile)
    {
        // Schema-driven mappings need no sample files.
        var policies = FeedPolicyConfiguration.Load(policyFile);
        var xdrSchemas = XdrLayoutConfiguration.Load(xdrLayoutFile);
        var referenceLayouts = ReferenceLayoutConfiguration.Load(referenceLayoutFile, xdrSchemas);
        foreach (var (feed, reference) in referenceLayouts)
        {
            foreach (var field in reference.Fields)
            {
                var rule = policies[feed].Get(field.Name);
                if (reference.Schema is not null && rule.Action != FieldAction.Keep &&
                    (rule.Identification != FieldIdentification.Known || rule.Action != FieldAction.Mask ||
                     rule.MaskCharacter[0] is < ' ' or > '~'))
                {
                    throw new InvalidDataException("Schema-driven binary fields require Keep or Known ASCII masks.");
                }
            }
        }
        return new(policies, xdrSchemas, referenceLayouts);
    }

    /// <summary>
    /// Creates the workflow registry using this configuration and the given protector.
    /// </summary>
    /// <param name="protector">The local configured field-protection service.</param>
    /// <returns>One workflow per supported feed.</returns>
    public FeedWorkflowRouter CreateRouter(IFeedProtector protector) =>
        FeedWorkflowRouter.Create(protector, Policies, feed => FeedCodecRegistry.Create(feed, XdrSchemas, ReferenceLayouts));

    #endregion
}
