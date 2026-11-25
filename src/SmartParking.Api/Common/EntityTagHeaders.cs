using System.Globalization;
using Microsoft.Net.Http.Headers;

namespace SmartParking.Api.Common;

/// <summary>
/// Strong ETags over the PostgreSQL <c>xmin</c> (spec 001): <c>"123"</c>. The version is not a hash of the
/// body, so the framework's ETag helpers are not used.
/// </summary>
internal static class EntityTagHeaders
{
    public static string Format(uint version) => $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    public static void SetETag(this HttpResponse response, uint version)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Headers.ETag = Format(version);
    }

    /// <summary>
    /// Reads <c>If-Match</c>. Absent: <c>true</c> with a <c>null</c> version (the handler answers 428).
    /// Anything other than one strong ETag of ours (a list, <c>*</c>, a weak tag, garbage): <c>false</c>, answered as 412.
    /// </summary>
    public static bool TryGetIfMatch(this HttpRequest request, out uint? version)
    {
        ArgumentNullException.ThrowIfNull(request);

        version = null;
        var values = request.Headers.IfMatch;
        if (values.Count == 0)
        {
            return true;
        }

        if (values.Count != 1
            || !EntityTagHeaderValue.TryParseStrictList(values, out var tags)
            || tags is not [{ IsWeak: false } tag]
            || !uint.TryParse(tag.Tag.AsSpan().Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        version = parsed;
        return true;
    }
}
