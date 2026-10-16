using System.Text;
using System.Text.Json;

namespace PresidioDemo.Feeds;

/// <summary>Reports feed data in workflow order while retaining all input and output bytes.</summary>
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
        AppendMessage(report, original);
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
            AppendFields(report, result.ProtectedFields);
            AppendMessage(report, original with { Content = result.Content });
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

    private static void AppendMessage(StringBuilder report, FeedMessage message)
    {
        report.AppendLine($"Complete feed data ({message.Content.Length} bytes):");
        AppendData(report, message.Content.Span);
        if (!Path.GetExtension(message.Delivery.OriginalName).Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        try
        {
            var envelope = FeedPayloadReader.Read(message);
            if (envelope.Format != "Raw")
            {
                report.AppendLine();
                report.AppendLine($"Decoded MQ payload ({envelope.Payload.Length} bytes):");
                AppendData(report, envelope.Payload);
            }
        }
        catch (InvalidDataException)
        {
            // Malformed envelopes still retain their complete original bytes above.
        }
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
