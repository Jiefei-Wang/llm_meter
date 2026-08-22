using System.Globalization;

namespace LLMMeter.Collection;

public sealed record PromSample(string Name, IReadOnlyDictionary<string, string> Labels, double Value)
{
    public bool TryGetLabel(string name, out string value) => Labels.TryGetValue(name, out value!);
}

/// <summary>
/// Lightweight Prometheus text-exposition parser. Tolerant: unknown or malformed
/// lines are skipped rather than failing the scrape.
/// </summary>
public static class PrometheusParser
{
    public static List<PromSample> Parse(string text)
    {
        var result = new List<PromSample>();
        if (string.IsNullOrEmpty(text)) return result;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith('#')) continue; // HELP/TYPE/comments ignored

            var sample = ParseLine(line);
            if (sample != null) result.Add(sample);
        }
        return result;
    }

    internal static PromSample? ParseLine(string line)
    {
        int nameEnd = 0;
        while (nameEnd < line.Length && (char.IsLetterOrDigit(line[nameEnd]) || line[nameEnd] == '_' || line[nameEnd] == ':'))
            nameEnd++;
        if (nameEnd == 0) return null;
        string name = line[..nameEnd];
        if (!char.IsLetter(line[0]) && line[0] != '_') return null;

        var labels = new Dictionary<string, string>();
        int pos = nameEnd;

        if (pos < line.Length && line[pos] == '{')
        {
            pos++;
            while (pos < line.Length && line[pos] != '}')
            {
                // skip whitespace and commas
                while (pos < line.Length && (line[pos] == ' ' || line[pos] == ',')) pos++;
                if (pos >= line.Length || line[pos] == '}') break;

                int labelStart = pos;
                while (pos < line.Length && (char.IsLetterOrDigit(line[pos]) || line[pos] == '_')) pos++;
                if (pos == labelStart) return null; // malformed
                string labelName = line[labelStart..pos];

                while (pos < line.Length && line[pos] == ' ') pos++;
                if (pos >= line.Length || line[pos] != '=') return null;
                pos++;
                while (pos < line.Length && line[pos] == ' ') pos++;
                if (pos >= line.Length || line[pos] != '"') return null;
                pos++;

                var sb = new System.Text.StringBuilder();
                while (pos < line.Length && line[pos] != '"')
                {
                    char c = line[pos];
                    if (c == '\\' && pos + 1 < line.Length)
                    {
                        char next = line[pos + 1];
                        switch (next)
                        {
                            case '\\': sb.Append('\\'); break;
                            case '"': sb.Append('"'); break;
                            case 'n': sb.Append('\n'); break;
                            default: sb.Append(next); break;
                        }
                        pos += 2;
                    }
                    else
                    {
                        sb.Append(c);
                        pos++;
                    }
                }
                if (pos >= line.Length) return null; // unterminated
                pos++; // closing quote
                labels[labelName] = sb.ToString();
            }
            if (pos >= line.Length || line[pos] != '}') return null;
            pos++;
        }

        while (pos < line.Length && (line[pos] == ' ' || line[pos] == '\t')) pos++;

        int valueEnd = pos;
        while (valueEnd < line.Length && line[valueEnd] != ' ' && line[valueEnd] != '\t')
            valueEnd++;
        string valueText = line[pos..valueEnd];

        double value;
        if (valueText is "NaN" or "+NaN") value = double.NaN;
        else if (valueText is "+Inf" or "Inf" or "inf" or "+inf") value = double.PositiveInfinity;
        else if (valueText is "-Inf" or "-inf") value = double.NegativeInfinity;
        else if (!double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return null;

        // Optional trailing timestamp — intentionally ignored.
        return new PromSample(name, labels, value);
    }
}
