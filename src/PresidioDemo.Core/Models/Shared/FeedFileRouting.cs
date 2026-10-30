using System.Text.RegularExpressions;

namespace PresidioDemo.Feeds.Shared;

/// <summary>
/// Chooses the feed for a sample or test file: by its feed folder, or else by a file name such as ACI_03.txt.
/// </summary>
public static class FeedFileRouting
{

    #region Methods

    /// <summary>
    /// Returns the feed for a file path.
    /// </summary>
    /// <param name="path">The file path, for example docs/sample-docs/PNR/PNR_02.txt or SEATS_02_Negative_Testing.txt.</param>
    /// <returns>The supported feed route.</returns>
    /// <exception cref="InvalidDataException">Neither the folder nor the file name identifies a feed.</exception>
    public static FeedType FromPath(string path)
    {
        string parent = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path))) ?? "";
        if (FeedCatalog.All.SingleOrDefault(f => f.FolderName.Equals(parent, StringComparison.OrdinalIgnoreCase)) is { } byFolder)
        {
            return byFolder.Type;
        }
        string route = Path.GetFileNameWithoutExtension(path).Replace('_', '-').ToUpperInvariant();
        route = Regex.Replace(route, "-NEGATIVE-TESTING$", "", RegexOptions.CultureInvariant);
        route = Regex.Replace(route, "-[0-9]+$", "", RegexOptions.CultureInvariant);
        return route switch
        {
            "ACI" => FeedType.ACI,
            "PNR" => FeedType.PNR,
            "PNR-LINKING" => FeedType.PnrLinking,
            "SEATS" => FeedType.Seats,
            "ETKT" or "TKT" => FeedType.ETKT,
            _ => throw new InvalidDataException($"Cannot route {Path.GetFileName(path)}: put it in a feed folder (ACI, ETKT, PNR, PNR-Linking, Seats) or name it after the feed.")
        };
    }

    /// <summary>
    /// Returns whether a file is a feed input: an IBM MQ export (.txt) or a raw payload (.bin).
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>Whether the extension is .txt or .bin.</returns>
    public static bool IsFeedFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".txt" or ".bin";

    #endregion
}
