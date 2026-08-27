using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AiMovieReviewLab.Core;

namespace AiMovieReviewLab.Core;

public sealed partial class SubtitleCleaner
{
    private static readonly string[] AdKeywords =
    [
        "字幕组", "压制", "翻译", "校对", "时间轴", "仅供学习", "严禁", "QQ群", "微信号", "公众号",
        "www.", ".com", ".cn", "论坛", "招募", "微博@"
    ];

    public async Task<SubtitleCleanResult> CleanAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        var (raw, encodingName) = Decode(bytes);
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        List<Cue> cues = ext switch
        {
            ".ass" or ".ssa" => ParseAss(raw),
            _ => ParseSrt(raw)
        };

        var output = new StringBuilder();
        var lastMinute = -1;
        string? previous = null;
        var kept = 0;

        foreach (var cue in cues.OrderBy(c => c.Start))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cleanedLines = CleanCueText(cue.Text);
            if (cleanedLines.Count == 0)
                continue;

            var text = string.Join("\n", cleanedLines);
            if (string.Equals(text, previous, StringComparison.Ordinal))
                continue;

            var minute = Math.Max(0, (int)cue.Start.TotalMinutes);
            if (minute != lastMinute)
            {
                if (output.Length > 0) output.AppendLine();
                output.Append('[').Append(cue.Start.ToString(@"hh\:mm")).AppendLine("]");
                lastMinute = minute;
            }

            output.AppendLine(text);
            previous = text;
            kept += cleanedLines.Count;
        }

        sw.Stop();
        var clean = output.ToString().Trim();
        return new SubtitleCleanResult
        {
            FilePath = filePath,
            EncodingName = encodingName,
            RawCharacters = raw.Length,
            CleanCharacters = clean.Length,
            ParsedBlocks = cues.Count,
            KeptLines = kept,
            ElapsedMs = sw.ElapsedMilliseconds,
            CleanText = clean,
            RawText = raw
        };
    }

    private static (string Text, string EncodingName) Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (Encoding.UTF8.GetString(bytes), "UTF-8 BOM");
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode.GetString(bytes), "UTF-16 LE");
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(bytes), "UTF-16 BE");

        try
        {
            return (new UTF8Encoding(false, true).GetString(bytes), "UTF-8");
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return (Encoding.GetEncoding(936).GetString(bytes), "GB18030/GBK fallback");
        }
    }

    private static List<Cue> ParseSrt(string raw)
    {
        var normalized = raw.Replace("\r\n", "\n").Replace('\r', '\n');
        var chunks = Regex.Split(normalized.Trim(), @"\n{2,}");
        var list = new List<Cue>();

        foreach (var chunk in chunks)
        {
            var lines = chunk.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (lines.Count == 0) continue;
            var timeIndex = lines.FindIndex(x => x.Contains("-->", StringComparison.Ordinal));
            if (timeIndex < 0 || !TryParseTimeRange(lines[timeIndex], out var start)) continue;
            var text = string.Join("\n", lines.Skip(timeIndex + 1));
            if (!string.IsNullOrWhiteSpace(text)) list.Add(new Cue(start, text));
        }

        return list;
    }

    private static List<Cue> ParseAss(string raw)
    {
        var list = new List<Cue>();
        foreach (var line in raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;
            var payload = line[(line.IndexOf(':') + 1)..].TrimStart();
            var parts = payload.Split(',', 10);
            if (parts.Length < 10 || !TimeSpan.TryParse(parts[1].Trim(), out var start)) continue;
            var text = parts[9].Replace("\\N", "\n", StringComparison.OrdinalIgnoreCase)
                               .Replace("\\n", "\n", StringComparison.OrdinalIgnoreCase)
                               .Replace("\\h", " ", StringComparison.OrdinalIgnoreCase);
            list.Add(new Cue(start, text));
        }
        return list;
    }

    private static bool TryParseTimeRange(string value, out TimeSpan start)
    {
        start = TimeSpan.Zero;
        var left = value.Split("-->", 2, StringSplitOptions.TrimEntries)[0].Replace(',', '.');
        return TimeSpan.TryParse(left, out start);
    }

    private static List<string> CleanCueText(string text)
    {
        text = AssTagRegex().Replace(text, string.Empty);
        text = HtmlTagRegex().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);

        var result = new List<string>();
        foreach (var original in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = Regex.Replace(original, @"\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (LooksLikeAdvertisement(line)) continue;
            if (LooksLikePureSongLyric(line)) continue;

            var chinese = ExtractChinesePreferredLine(line);
            if (string.IsNullOrWhiteSpace(chinese)) continue;
            if (result.Count > 0 && result[^1] == chinese) continue;
            result.Add(chinese);
        }
        return result;
    }

    private static string ExtractChinesePreferredLine(string line)
    {
        var hanCount = line.Count(c => c >= 0x4E00 && c <= 0x9FFF);
        if (hanCount == 0)
        {
            return string.Empty;
        }

        var match = Regex.Match(line, @"^(.*?[\u4e00-\u9fff][^|]*?)(?:\s{2,}|\s+[A-Z][A-Za-z'’ ,.!?\-]{12,})$");
        if (match.Success && match.Groups[1].Value.Count(c => c >= 0x4E00 && c <= 0x9FFF) >= 2)
            return match.Groups[1].Value.Trim();

        return line;
    }

    private static bool LooksLikeAdvertisement(string line) =>
        AdKeywords.Any(k => line.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikePureSongLyric(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return true;
        var musical = trimmed.Count(c => c is '♪' or '♫' or '♬' or '♩');
        return musical >= 1 && trimmed.Length < 120;
    }

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex AssTagRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    private sealed record Cue(TimeSpan Start, string Text);
}
