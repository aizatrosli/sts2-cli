using System.Text;

namespace Godot;

// Ports of GodotSharp's string extensions. The case conversions are native in GodotSharp
// (String::capitalize & co. in core/string/ustring.cpp); the rest mirror its managed code.
// Godot Engine is MIT-licensed, (c) Godot Engine contributors.
public static class StringExtensions
{
    // Preserve Godot resource/user prefixes rather than using OS path semantics.
    public static string PathJoin(this string instance, string file)
    {
        if (instance.Length == 0) return file;
        if (instance[^1] == '/' || (file.Length > 0 && file[0] == '/')) return instance + file;
        return instance + "/" + file;
    }

    /// <summary>"snake_case", "camelCase" or "ALLCAPS" → "Title Case With Spaces".</summary>
    public static string Capitalize(this string instance)
    {
        string words = SeparateCompoundWords(instance).Trim(StripChars);
        var ret = new StringBuilder();
        string[] slices = words.Split(' ');
        for (int i = 0; i < slices.Length; i++)
        {
            string slice = slices[i];
            if (slice.Length == 0) continue;
            if (i > 0) ret.Append(' ');
            ret.Append(char.ToUpperInvariant(slice[0])).Append(slice, 1, slice.Length - 1);
        }
        return ret.ToString();
    }

    public static string ToPascalCase(this string instance) => instance.Capitalize().Replace(" ", "");

    public static string ToCamelCase(this string instance)
    {
        string s = instance.ToPascalCase();
        return s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];
    }

    public static string ToSnakeCase(this string instance) => SeparateCompoundWords(instance).Replace(' ', '_');

    public static string ToKebabCase(this string instance) => SeparateCompoundWords(instance).Replace(' ', '-');

    public static string GetBaseDir(this string instance)
    {
        int index = instance.IndexOf("://", StringComparison.Ordinal);
        string prefix = string.Empty;
        string rest;
        if (index != -1)
        {
            int end = index + 3;
            rest = instance.Substring(end);
            prefix = instance.Substring(0, end);
        }
        else if (instance.StartsWith('/'))
        {
            rest = instance.Substring(1);
            prefix = "/";
        }
        else
        {
            rest = instance;
        }
        int sep = Math.Max(rest.LastIndexOf('/'), rest.LastIndexOf('\\'));
        return sep == -1 ? prefix : prefix + rest.Substring(0, sep);
    }

    public static string GetFile(this string instance)
    {
        int sep = Math.Max(instance.LastIndexOf('/'), instance.LastIndexOf('\\'));
        return sep == -1 ? instance : instance.Substring(sep + 1);
    }

    public static string GetExtension(this string instance)
    {
        int pos = instance.LastIndexOf('.');
        if (pos < 0 || pos < Math.Max(instance.LastIndexOf('/'), instance.LastIndexOf('\\'))) return string.Empty;
        return instance.Substring(pos + 1);
    }

    public static string GetBaseName(this string instance)
    {
        int index = instance.LastIndexOf('.');
        return index > 0 ? instance.Substring(0, index) : instance;
    }

    // String::strip_edges(): whitespace and control characters up to ' '.
    private static readonly char[] StripChars = Enumerable.Range(0, 33).Select(c => (char)c).ToArray();

    // String::_separate_compound_words(): split at aA, AAa, 2Aa, 2aa, A2 and a2 boundaries,
    // turn underscores into spaces, lower-case everything.
    private static string SeparateCompoundWords(string s)
    {
        if (s.Length == 0) return s;

        var result = new StringBuilder();
        int startIndex = 0;
        bool isPrevUpper = char.IsUpper(s[0]);
        bool isPrevLower = char.IsLower(s[0]);
        bool isPrevDigit = char.IsAsciiDigit(s[0]);

        for (int i = 1; i < s.Length; i++)
        {
            bool isCurrUpper = char.IsUpper(s[i]);
            bool isCurrLower = char.IsLower(s[i]);
            bool isCurrDigit = char.IsAsciiDigit(s[i]);
            bool isNextLower = i + 1 < s.Length && char.IsLower(s[i + 1]);

            bool condA = isPrevLower && isCurrUpper; // aA
            bool condB = (isPrevUpper || isPrevDigit) && isCurrUpper && isNextLower; // AAa, 2Aa
            bool condC = isPrevDigit && isCurrLower && isNextLower; // 2aa
            bool condD = (isPrevUpper || isPrevLower) && isCurrDigit; // A2, a2

            if (condA || condB || condC || condD)
            {
                result.Append(s, startIndex, i - startIndex).Append(' ');
                startIndex = i;
            }

            isPrevUpper = isCurrUpper;
            isPrevLower = isCurrLower;
            isPrevDigit = isCurrDigit;
        }

        result.Append(s, startIndex, s.Length - startIndex);
        return result.ToString().Replace('_', ' ').ToLowerInvariant();
    }
}
