using System.Net;
using System.Text;

namespace MecchaCamouflage.Core;

/// <summary>
/// Applies host-owned document policy before any module-authored script or resource can run.
/// </summary>
public static class ModuleDocumentSecurity
{
    private const int MaxPolicyLength = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly byte[] Utf8Preamble = [0xEF, 0xBB, 0xBF];

    public static bool TryInjectContentSecurityPolicy(
        byte[] entryBytes,
        string policy,
        out byte[] securedEntryBytes)
    {
        ArgumentNullException.ThrowIfNull(entryBytes);
        securedEntryBytes = [];
        if (string.IsNullOrWhiteSpace(policy) || policy.Length > MaxPolicyLength ||
            policy.Any(char.IsControl))
        {
            return false;
        }

        string html;
        try
        {
            html = StrictUtf8.GetString(entryBytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        var headContentOffset = FindHeadContentOffset(html);
        if (headContentOffset < 0)
            return false;

        var encodedPolicy = WebUtility.HtmlEncode(policy);
        var meta =
            "\n<meta charset=\"utf-8\">" +
            "\n<meta name=\"referrer\" content=\"no-referrer\">" +
            $"\n<meta http-equiv=\"Content-Security-Policy\" content=\"{encodedPolicy}\">";
        var encodedEntry = StrictUtf8.GetBytes(html.Insert(headContentOffset, meta));
        securedEntryBytes = encodedEntry.AsSpan().StartsWith(Utf8Preamble)
            ? encodedEntry
            : [.. Utf8Preamble, .. encodedEntry];
        return true;
    }

    private static int FindHeadContentOffset(string html)
    {
        var offset = html.Length > 0 && html[0] == '\uFEFF' ? 1 : 0;
        var sawHtml = false;
        while (offset < html.Length)
        {
            while (offset < html.Length && char.IsWhiteSpace(html[offset]))
                ++offset;
            if (offset >= html.Length)
                return -1;

            if (StartsWith(html, offset, "<!--"))
            {
                var commentEnd = html.IndexOf("-->", offset + 4, StringComparison.Ordinal);
                if (commentEnd < 0)
                    return -1;
                offset = commentEnd + 3;
                continue;
            }

            if (StartsWithTag(html, offset, "!doctype"))
            {
                offset = FindTagEnd(html, offset);
                if (offset < 0)
                    return -1;
                continue;
            }

            if (!sawHtml && StartsWithTag(html, offset, "html"))
            {
                sawHtml = true;
                offset = FindTagEnd(html, offset);
                if (offset < 0)
                    return -1;
                continue;
            }

            if (StartsWithTag(html, offset, "head"))
                return FindTagEnd(html, offset);

            // Fail closed if executable markup or body content appears before an explicit head.
            return -1;
        }
        return -1;
    }

    private static bool StartsWith(string value, int offset, string expected) =>
        value.AsSpan(offset).StartsWith(expected, StringComparison.Ordinal);

    private static bool StartsWithTag(string html, int offset, string tagName)
    {
        if (offset >= html.Length || html[offset] != '<' ||
            !html.AsSpan(offset + 1).StartsWith(tagName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var boundary = offset + 1 + tagName.Length;
        return boundary < html.Length &&
            (char.IsWhiteSpace(html[boundary]) || html[boundary] is '>' or '/');
    }

    private static int FindTagEnd(string html, int offset)
    {
        var quote = '\0';
        for (var index = offset + 1; index < html.Length; ++index)
        {
            var value = html[index];
            if (quote != '\0')
            {
                if (value == quote)
                    quote = '\0';
                continue;
            }

            if (value is '\'' or '"')
            {
                quote = value;
                continue;
            }
            if (value == '>')
                return index + 1;
        }
        return -1;
    }
}
