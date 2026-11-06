using PresidioDemo.Feeds.Shared;

namespace PresidioDemo.Protection;

#region Interfaces

/// <summary>
/// Protects every decoded string of one parsed message and reports what happened to each.
/// </summary>
public interface IFeedProtector
{

    #region Methods

    /// <summary>
    /// Protects one parsed message.
    /// </summary>
    /// <param name="feed">The feed route.</param>
    /// <param name="parsed">The decoded-text parse.</param>
    /// <param name="cancellationToken">The token that interrupts processing while retaining recoverable input.</param>
    /// <returns>The protected value of every field and one value-free event per decoded string.</returns>
    /// <exception cref="InvalidDataException">A structural failure; the message is dead-lettered.</exception>
    ValueTask<DetectionOutcome> ProtectAsync(FeedType feed, ParsedFeed parsed, CancellationToken cancellationToken);

    #endregion
}

#endregion
