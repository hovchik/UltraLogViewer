using System.Globalization;
using System.Text.RegularExpressions;

namespace UltraLogViewer.Core;

/// <summary>Timestamp recognition for the formats log files commonly use. Times are Unix epoch milliseconds.</summary>
public static partial class Timestamps
{
    /// <summary>Marker for "no timestamp" in index arrays.</summary>
    public const long None = long.MinValue;

    private static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    [GeneratedRegex(@"^(\d{4})[-/.](\d{2})[-/.](\d{2})[T ](\d{2}):(\d{2}):(\d{2})(?:[.,](\d{1,9}))?(?: ?(Z|[+-]\d{2}:?\d{2}))?")]
    private static partial Regex IsoRe();
    [GeneratedRegex(@"^(\d{2})/([A-Za-z]{3})/(\d{4}):(\d{2}):(\d{2}):(\d{2})(?:[.,](\d{1,9}))?(?: ([+-]\d{4}))?")]
    private static partial Regex ApacheRe();
    [GeneratedRegex(@"^([A-Z][a-z]{2}) {1,2}(\d{1,2}) (\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,9}))?")]
    private static partial Regex SyslogRe();
    [GeneratedRegex(@"^(\d{2})([./-])(\d{2})\2(\d{4})[ T](\d{2}):(\d{2}):(\d{2})(?:[.,](\d{1,9}))?")]
    private static partial Regex DmyRe();
    [GeneratedRegex(@"^(\d{2}):(\d{2}):(\d{2})(?:[.,](\d{1,9}))?(?!\d)")]
    private static partial Regex TimeOnlyRe();
    [GeneratedRegex(@"^(\d{10}|\d{13})(?:\.(\d{1,9}))?(?!\d)")]
    private static partial Regex EpochRe();

    private sealed record HourCache(long Key, long Ms);
    private static HourCache? _hour;

    private static bool IsDigit(char c) => c >= '0' && c <= '9';
    private static int D2(ReadOnlySpan<char> s, int i) => (s[i] - '0') * 10 + s[i + 1] - '0';

    private static int Frac(string f)
    {
        if (f.Length == 0) return 0;
        var s = (f + "00")[..3];
        return int.Parse(s, CultureInfo.InvariantCulture);
    }

    private static int? TzOffsetMinutes(ReadOnlySpan<char> tz)
    {
        if (tz.Length == 0) return null;
        if (tz is "Z") return 0;
        var sign = tz[0] == '-' ? -1 : 1;
        var digits = tz[1..].ToString().Replace(":", "");
        if (digits.Length != 4 || !int.TryParse(digits, out var v)) return null;
        return sign * (v / 100 * 60 + v % 100);
    }

    /// <summary>Builds epoch ms from local-or-offset components; month is 1-based. Returns <see cref="None"/> when invalid.</summary>
    public static long Build(int y, int mo, int d, int h, int mi, int s, int ms, int? tzMinutes)
    {
        if (y < 1 || y > 9999 || mo < 1 || mo > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo) || h > 23 || mi > 59 || s > 60) return None;
        if (s == 60) s = 59;
        if (tzMinutes is int off)
        {
            var utc = new DateTime(y, mo, d, h, mi, s, DateTimeKind.Utc);
            return new DateTimeOffset(utc).ToUnixTimeMilliseconds() + ms - off * 60_000L;
        }
        // Local-time conversion is the slow part of parsing millions of lines; the offset only
        // changes at DST boundaries, so cache the epoch value of each local hour.
        var key = ((y * 12L + mo) * 32 + d) * 24 + h;
        var cache = _hour;
        if (cache is null || cache.Key != key)
        {
            var local = new DateTime(y, mo, d, h, 0, 0, DateTimeKind.Unspecified);
            var offset = TimeZoneInfo.Local.GetUtcOffset(local);
            cache = new HourCache(key, new DateTimeOffset(local, offset).ToUnixTimeMilliseconds());
            _hour = cache;
        }
        return cache.Ms + (mi * 60L + s) * 1000 + ms;
    }

    /// <summary>Hand-rolled parser for "YYYY-MM-DD[T ]HH:MM:SS[.fff][tz]", much faster than a regex.</summary>
    private static bool FastIso(ReadOnlySpan<char> s, out long result, out int length)
    {
        result = None;
        length = 0;
        if (s.Length < 19) return false;
        ReadOnlySpan<int> digits = [0, 1, 2, 3, 5, 6, 8, 9, 11, 12, 14, 15, 17, 18];
        foreach (var i in digits) if (!IsDigit(s[i])) return false;
        var sep = s[4];
        if ((sep != '-' && sep != '/' && sep != '.') || s[7] != sep) return false;
        if ((s[10] != 'T' && s[10] != ' ') || s[13] != ':' || s[16] != ':') return false;
        var p = 19;
        var ms = 0;
        if (p + 1 < s.Length && (s[p] == '.' || s[p] == ',') && IsDigit(s[p + 1]))
        {
            p++;
            var n = 0;
            while (p < s.Length && IsDigit(s[p]))
            {
                if (n < 3) ms = ms * 10 + s[p] - '0';
                n++;
                p++;
            }
            if (n == 1) ms *= 100;
            else if (n == 2) ms *= 10;
        }
        if (p < s.Length && IsDigit(s[p])) return false;
        int? tz = null;
        var j = p < s.Length && s[p] == ' ' ? p + 1 : p;
        if (j < s.Length && s[j] == 'Z')
        {
            tz = 0;
            p = j + 1;
        }
        else if (j + 2 < s.Length && (s[j] == '+' || s[j] == '-') && IsDigit(s[j + 1]) && IsDigit(s[j + 2]))
        {
            var colon = j + 3 < s.Length && s[j + 3] == ':' ? 1 : 0;
            if (j + 4 + colon < s.Length && IsDigit(s[j + 3 + colon]) && IsDigit(s[j + 4 + colon]))
            {
                tz = TzOffsetMinutes(s.Slice(j, 5 + colon));
                p = j + 5 + colon;
            }
        }
        var y = D2(s, 0) * 100 + D2(s, 2);
        result = Build(y, D2(s, 5), D2(s, 8), D2(s, 11), D2(s, 14), D2(s, 17), ms, tz);
        length = p;
        return result != None;
    }

    /// <summary>Recognises a timestamp at the start of <paramref name="s"/> (optionally inside "[...]").</summary>
    public static bool TryMatch(ReadOnlySpan<char> s, int defaultYear, out long ms, out int length, bool allowEpoch = false)
    {
        ms = None;
        length = 0;
        if (s.IsEmpty) return false;
        var skip = s[0] == '[' ? 1 : 0;
        if (s.Length <= skip) return false;
        var c = s[skip];
        var digit = IsDigit(c);
        if (!digit && !(c >= 'A' && c <= 'Z')) return false; // fast path for continuation lines
        var t = s[skip..];
        if (digit && FastIso(t, out ms, out length))
        {
            length += skip;
            if (skip == 1 && length < s.Length && s[length] == ']') length++;
            return true;
        }
        var head = t.Length > 48 ? t[..48].ToString() : t.ToString();
        Match m;
        if (digit && (m = IsoRe().Match(head)).Success)
            ms = Build(I(m, 1), I(m, 2), I(m, 3), I(m, 4), I(m, 5), I(m, 6), Frac(m.Groups[7].Value), TzOffsetMinutes(m.Groups[8].Value));
        else if (digit && (m = ApacheRe().Match(head)).Success)
        {
            var mo = Array.IndexOf(Months, m.Groups[2].Value.ToLowerInvariant());
            if (mo < 0) return false;
            ms = Build(I(m, 3), mo + 1, I(m, 1), I(m, 4), I(m, 5), I(m, 6), Frac(m.Groups[7].Value), TzOffsetMinutes(m.Groups[8].Value));
        }
        else if (!digit && (m = SyslogRe().Match(head)).Success)
        {
            var mo = Array.IndexOf(Months, m.Groups[1].Value.ToLowerInvariant());
            if (mo < 0) return false;
            ms = Build(defaultYear, mo + 1, I(m, 2), I(m, 3), I(m, 4), I(m, 5), Frac(m.Groups[6].Value), null);
        }
        else if (digit && (m = DmyRe().Match(head)).Success)
        {
            // 31.12.2024 / 31-12-2024 are day-first; 12/31/2024 is month-first.
            int a = I(m, 1), b = I(m, 3);
            var (d, mo) = m.Groups[2].Value == "/" && a <= 12 && b <= 31 ? (b, a) : (a, b);
            ms = Build(I(m, 4), mo, d, I(m, 5), I(m, 6), I(m, 7), Frac(m.Groups[8].Value), null);
        }
        else if (digit && (m = TimeOnlyRe().Match(head)).Success)
        {
            int h = I(m, 1), mi = I(m, 2), sec = I(m, 3);
            if (h > 23 || mi > 59 || sec > 60) return false;
            ms = ((h * 60L + mi) * 60 + sec) * 1000 + Frac(m.Groups[4].Value);
        }
        else if (digit && allowEpoch && (m = EpochRe().Match(head)).Success)
            ms = m.Groups[1].Value.Length == 13 ? long.Parse(m.Groups[1].Value) : long.Parse(m.Groups[1].Value) * 1000 + Frac(m.Groups[2].Value);
        else
            return false;
        if (ms == None) return false;
        length = m.Length + skip;
        if (skip == 1 && length < s.Length && s[length] == ']') length++;
        return true;
    }

    private static int I(Match m, int g) => int.Parse(m.Groups[g].ValueSpan, CultureInfo.InvariantCulture);

    /// <summary>Parses a standalone timestamp value (e.g. a JSON field). Returns <see cref="None"/> if unrecognised.</summary>
    public static long Parse(string? value, int defaultYear = 0)
    {
        if (string.IsNullOrWhiteSpace(value)) return None;
        var s = value.Trim();
        if (TryMatch(s, defaultYear == 0 ? DateTime.Now.Year : defaultYear, out var ms, out _, allowEpoch: true)) return ms;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dto) ? dto.ToUnixTimeMilliseconds() : None;
    }

    public static long FromNumber(double v) => v > 1e12 ? (long)v : (long)(v * 1000);

    public static string Format(long ms)
    {
        if (ms == None) return "";
        return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
    }

    /// <summary>Full date and time, local or UTC (UTC ends with Z).</summary>
    public static string Format(long ms, bool utc)
    {
        if (!utc) return Format(ms);
        return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "Z";
    }

    /// <summary>Time of day with milliseconds, local or UTC (UTC ends with Z).</summary>
    public static string FormatTimeOfDay(long ms, bool utc)
    {
        var d = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        return utc ? d.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "Z" : d.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
    }

    /// <summary>A duration with an explicit + for positive values ("+1.250 s", "-3m 02.000s", "0 ms").</summary>
    public static string FormatSigned(long ms) => ms > 0 ? "+" + FormatDuration(ms) : FormatDuration(ms);

    /// <summary>A short gap label for the gutter: "+850ms", "+32s", "+2m", "+3h", "+1d".</summary>
    public static string FormatGap(long ms) => ms switch
    {
        < 1000 => $"+{ms}ms",
        < 60_000 => $"+{ms / 1000}s",
        < 3_600_000 => $"+{ms / 60_000}m",
        < 86_400_000 => $"+{ms / 3_600_000}h",
        _ => $"+{ms / 86_400_000}d",
    };

    public static string FormatDuration(long ms)
    {
        var sign = ms < 0 ? "-" : "";
        ms = Math.Abs(ms);
        if (ms < 1000) return $"{sign}{ms} ms";
        if (ms < 60000) return $"{sign}{ms / 1000.0:0.000} s".Replace(',', '.');
        var s = ms / 1000;
        long h = s / 3600, mi = s % 3600 / 60, sec = s % 60;
        var rest = $"{mi:00}m {sec:00}.{ms % 1000:000}s";
        return h > 0 ? $"{sign}{h}h {rest}" : $"{sign}{rest}";
    }
}
