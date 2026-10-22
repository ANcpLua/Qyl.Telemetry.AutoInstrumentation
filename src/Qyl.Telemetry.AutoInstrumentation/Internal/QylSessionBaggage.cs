using System.Buffers;
using System.Diagnostics;
using SessionAttributes = Qyl.Telemetry.SemanticConventions.Incubating.Attributes.Session.SessionAttributes;

namespace Qyl.Telemetry.AutoInstrumentation.Internal;

/// <summary>
/// The session spine: the W3C <c>baggage</c> member <c>session.id</c> an agent sends becomes the
/// <c>session.id</c> tag of the ASP.NET Core server span, and the session processor of
/// <c>Qyl.Telemetry.Hosting</c> copies it to every in-process descendant. Every ASP.NET Core service
/// joins the spine without code.
/// </summary>
/// <remarks>
/// <para>
/// The header is not parsed here: the hosting layer parsed it when it started the server activity,
/// and <see cref="Activity.GetBaggageItem"/> returns the decoded, first-wins value. What this adds is
/// the tag and the admission rule.
/// </para>
/// <para>
/// A value is admitted only as a session id: unreserved URI characters (<c>A-Z a-z 0-9 . _ ~ -</c>),
/// one to <see cref="MaxSessionIdLength"/> of them, and not dots alone. The id becomes a storage key and
/// a path segment in the collector, so anything else is left off rather than truncated or escaped; a
/// request without the member, or with an existing <c>session.id</c> tag, is left alone.
/// </para>
/// </remarks>
internal static class QylSessionBaggage
{
    /// <summary>The longest <c>session.id</c> admitted from the wire.</summary>
    private const int MaxSessionIdLength = 128;

    private static readonly SearchValues<char> Unreserved =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._~-");

    internal static void Stamp(Activity activity)
    {
        if (activity.GetTagItem(SessionAttributes.Id) is not null)
            return;

        var sessionId = WithoutMemberProperties(activity.GetBaggageItem(SessionAttributes.Id));
        if (IsSessionId(sessionId))
            activity.SetTag(SessionAttributes.Id, sessionId);
    }

    // W3C allows ;-separated properties on a member and .NET leaves them in the value, so
    // "session.id=agent-1;p=x" arrives as "agent-1;p=x". The optional whitespace before the separator
    // goes with it; nothing else is trimmed.
    private static string? WithoutMemberProperties(string? member)
    {
        if (member is null)
            return null;

        var properties = member.IndexOf(';', StringComparison.Ordinal);
        return properties < 0 ? member : member[..properties].TrimEnd(' ', '\t');
    }

    private static bool IsSessionId(string? candidate)
    {
        if (candidate is not { Length: > 0 and <= MaxSessionIdLength })
            return false;

        return !candidate.AsSpan().ContainsAnyExcept(Unreserved) && candidate.AsSpan().ContainsAnyExcept('.');
    }
}
