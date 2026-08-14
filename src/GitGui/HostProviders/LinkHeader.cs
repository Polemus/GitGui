using System;
using System.Collections.Generic;

namespace GitGui.HostProviders;

/// <summary>
/// Reads the <c>Link</c> header that says where the next page of a list is.
/// </summary>
/// <remarks>
/// RFC 5988, and the reason it is worth having rather than counting pages ourselves:
/// every site we talk to paginates differently - GitHub and GitLab count pages with
/// <c>page</c>, Gitea takes a <c>limit</c>, and a site added from a manifest could do
/// something else again - but all of them answer with the same header naming the next
/// URL. Following it needs to know nothing about any of their schemes.
/// </remarks>
public static class LinkHeader
{
    /// <summary>
    /// The URL of the next page, or null at the end of the list.
    /// </summary>
    /// <param name="values">The header's values, as HttpHeaders hands them over.</param>
    /// <param name="relativeTo">
    /// Resolves a relative URL. Some servers send one, and the spec allows it.
    /// </param>
    public static Uri? Next(IEnumerable<string>? values, Uri? relativeTo = null)
    {
        if (values is null)
            return null;

        foreach (var value in values)
        {
            foreach (var link in Split(value))
            {
                if (IsNext(link) && TryUrl(link, relativeTo, out var url))
                    return url;
            }
        }

        return null;
    }

    /// <summary>
    /// Splits on the commas that separate links, and not on any inside the URL - which
    /// is why this is not <c>string.Split(',')</c>. A URL holding a comma is ordinary
    /// in a query string, and splitting through one silently loses the last page.
    /// </summary>
    private static List<string> Split(string header)
    {
        var links = new List<string>();
        var start = 0;
        var inUrl = false;

        for (var i = 0; i < header.Length; i++)
        {
            switch (header[i])
            {
                case '<': inUrl = true; break;
                case '>': inUrl = false; break;
                case ',' when !inUrl:
                    links.Add(header[start..i]);
                    start = i + 1;
                    break;
            }
        }

        links.Add(header[start..]);
        return links;
    }

    /// <summary>
    /// True when a link's parameters include <c>rel="next"</c>. The quotes are optional
    /// in the spec and both forms turn up, so the comparison ignores them.
    /// </summary>
    private static bool IsNext(string link)
    {
        foreach (var part in link.Split(';'))
        {
            var parameter = part.Trim();
            if (!parameter.StartsWith("rel", StringComparison.OrdinalIgnoreCase))
                continue;

            var equals = parameter.IndexOf('=');
            if (equals < 0)
                continue;

            if (parameter[(equals + 1)..].Trim().Trim('"', '\'')
                .Equals("next", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryUrl(string link, Uri? relativeTo, out Uri url)
    {
        url = null!;

        var open = link.IndexOf('<');
        var close = link.IndexOf('>', open + 1);
        if (open < 0 || close < 0)
            return false;

        var text = link[(open + 1)..close].Trim();

        // Absolute first, then relative, and the scheme is checked on both - resolving
        // against a base URL still lets an absolute reference through.
        //
        // That check is not fussiness. On Unix a leading slash parses as an *absolute*
        // URI: "/api/v1/user/repos?page=3" becomes file:///api/v1/user/repos%3Fpage=3,
        // which is why the absolute attempt has to be able to fail and fall through to
        // the relative one rather than winning by parsing. Without it a relative link
        // would have the next page fetched off the local filesystem, and a server could
        // point this anywhere it liked just by answering with a Link header.
        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute) && IsWeb(absolute))
        {
            url = absolute;
            return true;
        }

        if (relativeTo is not null && Uri.TryCreate(relativeTo, text, out var resolved) && IsWeb(resolved))
        {
            url = resolved;
            return true;
        }

        return false;
    }

    private static bool IsWeb(Uri url)
        => url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps;
}
