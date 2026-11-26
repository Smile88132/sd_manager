using System.Globalization;
using System.Text.RegularExpressions;

namespace SdKontrol.API.Services;

/// <summary>Excel hücre değerlerini kültürden bağımsız ve güvenli şekilde dönüştürür.</summary>
public static class CellParser
{
    private static readonly CultureInfo Tr = new("tr-TR");

    public static string? Text(object? v)
    {
        if (v == null) return null;
        if (v is double d && Math.Abs(d % 1) < 1e-9 && Math.Abs(d) < 1e15)
            return ((long)d).ToString(CultureInfo.InvariantCulture);
        var s = Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    /// <summary>"1.128,00" → 1128 | "1.128" → 1128 | "45900" → 45900 | "420,00" → 420 | 12.5 → 12.5</summary>
    public static decimal? Number(object? v)
    {
        if (v == null) return null;
        switch (v)
        {
            case double d: return (decimal)d;
            case decimal m: return m;
            case int i: return i;
            case long l: return l;
            case float f: return (decimal)f;
        }
        var s = Text(v);
        if (string.IsNullOrEmpty(s)) return null;
        s = s.Replace(" ", "").Replace("\u00A0", "");
        s = Regex.Replace(s, @"[^\d,.\-]", "");
        if (string.IsNullOrEmpty(s)) return null;

        if (s.Contains(','))
        {
            // Türkçe biçim: nokta binlik, virgül ondalık
            return decimal.TryParse(s, NumberStyles.Number, Tr, out var trVal) ? trVal : null;
        }
        if (s.Contains('.'))
        {
            var parts = s.Split('.');
            bool allThousandGroups = parts.Length > 1 && parts.Skip(1).All(p => p.Length == 3);
            if (allThousandGroups)
                s = s.Replace(".", ""); // "1.128" → 1128 (binlik ayıracı)
        }
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var inv) ? inv : null;
    }

    public static int? Int(object? v)
    {
        var n = Number(v);
        return n.HasValue ? (int)Math.Round(n.Value) : null;
    }

    public static DateTime? Date(object? v)
    {
        if (v == null) return null;
        if (v is DateTime dt) return dt;
        if (v is double d && d > 1000 && d < 200000)
        {
            try { return DateTime.FromOADate(d); } catch { return null; }
        }
        var s = Text(v);
        if (string.IsNullOrEmpty(s)) return null;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var oa) && oa > 1000 && oa < 200000 && !s.Contains('.') && !s.Contains('/'))
        {
            try { return DateTime.FromOADate(oa); } catch { }
        }
        string[] fmts = { "dd.MM.yyyy", "d.M.yyyy", "dd.MM.yyyy HH:mm:ss", "dd.MM.yyyy HH:mm", "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "dd/MM/yyyy", "MM/dd/yyyy" };
        if (DateTime.TryParseExact(s, fmts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var ex)) return ex;
        if (DateTime.TryParse(s, Tr, DateTimeStyles.None, out var trd)) return trd;
        return null;
    }

    /// <summary>"24" | "24 AY" | "24 ay" → 24 ; "YOK" | "0" → 0 ; tanımsız → null</summary>
    public static int? GarantiAy(object? v)
    {
        var s = Text(v);
        if (string.IsNullOrEmpty(s)) return null;
        var up = s.ToUpper(Tr);
        if (up.Contains("YOK")) return 0;
        var m = Regex.Match(up, @"\d+");
        if (!m.Success) return null;
        var n = int.Parse(m.Value, CultureInfo.InvariantCulture);
        if (up.Contains("YIL")) n *= 12;
        return n;
    }

    /// <summary>Türkçe/İngilizce bağımsız başlık eşleştirme anahtarı.</summary>
    public static string HeaderKey(string? h)
    {
        if (string.IsNullOrWhiteSpace(h)) return string.Empty;
        var s = h.Trim()
            .Replace('İ', 'i').Replace('I', 'i').Replace('ı', 'i')
            .Replace('Ş', 's').Replace('ş', 's')
            .Replace('Ğ', 'g').Replace('ğ', 'g')
            .Replace('Ü', 'u').Replace('ü', 'u')
            .Replace('Ö', 'o').Replace('ö', 'o')
            .Replace('Ç', 'c').Replace('ç', 'c')
            .ToLowerInvariant();
        s = Regex.Replace(s, @"[^a-z0-9]+", " ").Trim();
        return s;
    }
}
