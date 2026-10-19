using System.Text;
using System.Text.Json;

namespace PresidioDemo.Feeds;

/// <summary>Reports complete input, decoded payload, plain text and parsed fields, followed by output payload and complete feed data.</summary>
public static class FeedTransactionLog
{
    private const string Divider = "========================================================================";

    public static string Format(FeedMessage original, FeedWorkflowResult? result, string? reason)
    {
        var report = new StringBuilder();
        report.AppendLine($"Transaction: {original.Delivery.MessageId:N}");
        report.AppendLine($"Feed: {FeedCatalog.Get(original.Delivery.Feed).FolderName}");
        report.AppendLine($"Source file: {JsonSerializer.Serialize(original.Delivery.OriginalName)}");
        report.AppendLine();
        AppendHeading(report, "BEFORE ANONYMIZATION");
        AppendMessage(report, original, completeFeedFirst: true);
        report.AppendLine();
        AppendFields(report, result?.OriginalFields);
        report.AppendLine();
        AppendHeading(report, "AFTER ANONYMIZATION");
        if (result is null)
        {
            report.AppendLine("No validated anonymized output was produced.");
            report.AppendLine($"Reason: {reason ?? "Processing failed."}");
        }
        else
        {
            AppendMessage(report, original with { Content = result.Content }, completeFeedFirst: false);
        }
        return report.ToString();
    }

    private static void AppendHeading(StringBuilder report, string heading)
    {
        report.AppendLine(Divider);
        report.AppendLine(heading);
        report.AppendLine(Divider);
        report.AppendLine();
    }

    private static void AppendFields(StringBuilder report, IReadOnlyList<FeedField>? fields)
    {
        if (fields is null)
        {
            return;
        }
        report.AppendLine("Parsed fields (location | policy field | value):");
        foreach (var field in fields)
        {
            report.AppendLine($"{JsonSerializer.Serialize(field.Id)} | {JsonSerializer.Serialize(field.Name)} | {JsonSerializer.Serialize(field.Value)}");
        }
        report.AppendLine();
    }

    private static void AppendMessage(StringBuilder report, FeedMessage message, bool completeFeedFirst)
    {
        if (completeFeedFirst) AppendCompleteFeed(report, message);
        ReadOnlyMemory<byte> plainTextPayload = message.Content;
        bool decoded = false;
        if (Path.GetExtension(message.Delivery.OriginalName).Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var envelope = FeedPayloadReader.Read(message);
                if (envelope.Format != "Raw")
                {
                    if (completeFeedFirst) report.AppendLine();
                    report.AppendLine($"Decoded MQ payload ({envelope.Payload.Length} bytes):");
                    AppendData(report, envelope.Payload);
                    plainTextPayload = envelope.Payload;
                    decoded = true;
                }
            }
            catch (InvalidDataException)
            {
                // Malformed envelopes still retain their complete bytes.
            }
        }
        if (completeFeedFirst)
        {
            report.AppendLine();
            AppendPlainText(report, plainTextPayload.Span);
        }
        else
        {
            if (decoded) report.AppendLine();
            AppendCompleteFeed(report, message);
        }
    }

    private static void AppendPlainText(StringBuilder report, ReadOnlySpan<byte> bytes)
    {
        report.AppendLine("Plain text:");
        // Keep readable UTF-8 together, including text crossing hex-dump rows.
        // Binary controls and invalid UTF-8 become spaces; the dumps retain exact bytes.
        foreach (char character in Encoding.UTF8.GetString(bytes))
        {
            report.Append(character == '\uFFFD' ||
                (char.IsControl(character) && character is not ('\r' or '\n' or '\t'))
                ? ' ' : character);
        }
        report.AppendLine();
    }

    private static void AppendCompleteFeed(StringBuilder report, FeedMessage message)
    {
        report.AppendLine($"Complete feed data ({message.Content.Length} bytes):");
        AppendData(report, message.Content.Span);
    }

    private static void AppendData(StringBuilder report, ReadOnlySpan<byte> bytes)
    {
        try
        {
            string text = new UTF8Encoding(false, true).GetString(bytes);
            if (!text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            {
                report.AppendLine("UTF-8 text:");
                report.AppendLine(text);
                return;
            }
        }
        catch (DecoderFallbackException)
        {
            // Binary content uses lossless hexadecimal with a readable ASCII column.
        }
        report.AppendLine("Hex dump (offset | bytes | ASCII):");
        for (int offset = 0; offset < bytes.Length; offset += 16)
        {
            var row = bytes.Slice(offset, Math.Min(16, bytes.Length - offset));
            report.Append(offset.ToString("X8")).Append("  ");
            for (int index = 0; index < 16; index++)
            {
                report.Append(index < row.Length ? row[index].ToString("X2") + " " : "   ");
            }
            report.Append(" | ");
            foreach (byte value in row)
            {
                report.Append(value is >= 32 and <= 126 ? (char)value : '.');
            }
            report.AppendLine();
        }
    }
}
