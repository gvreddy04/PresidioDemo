using PresidioDemo.Feeds.Shared;
using PresidioDemo.Protection;

namespace PresidioDemo.Pipeline;

/// <summary>
/// The validated per-feed recognizer sets that drive every feed workflow (WR-014, WR-017).
/// </summary>
/// <param name="Recognizers">The recognizer set of each feed.</param>
public sealed record FeedPipelineConfiguration(IReadOnlyDictionary<FeedType, FeedRecognizerSet> Recognizers)
{

    #region Methods

    /// <summary>
    /// Loads and validates one recognizer XML file per feed before any input is read (WR-011 carried over).
    /// </summary>
    /// <param name="recognizerDirectory">The directory holding <c>aci.xml</c>, <c>etkt.xml</c>, <c>pnr.xml</c>,
    /// <c>pnr-linking.xml</c> and <c>seats.xml</c>.</param>
    /// <returns>The validated configuration.</returns>
    /// <exception cref="InvalidDataException">A file is missing or invalid.</exception>
    public static FeedPipelineConfiguration Load(string recognizerDirectory) => new(RecognizerConfiguration.Load(recognizerDirectory));

    /// <summary>
    /// Creates the detecting protector for these recognizer sets. Call <see cref="DetectingFeedProtector.Configure"/>
    /// after <see cref="PythonHost.Start"/>.
    /// </summary>
    /// <returns>The protector.</returns>
    public DetectingFeedProtector CreateProtector() => new(Recognizers);

    /// <summary>
    /// Creates the workflow registry using the given protector.
    /// </summary>
    /// <param name="protector">The local detection and protection service.</param>
    /// <returns>One workflow per supported feed.</returns>
    public FeedWorkflowRouter CreateRouter(IFeedProtector protector) => FeedWorkflowRouter.Create(protector, FeedCodecRegistry.Create);

    #endregion
}
