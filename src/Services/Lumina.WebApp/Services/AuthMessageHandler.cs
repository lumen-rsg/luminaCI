using System.Net;
using System.Net.Http.Headers;

namespace Lumina.WebApp.Services;

/// <summary>
/// Transparently refreshes an expired access cookie.
///
/// The browser attaches the HttpOnly auth cookies automatically (the handler no
/// longer injects a Bearer header). When a request comes back 401, the handler
/// performs a single <c>/api/auth/refresh</c> via <see cref="AuthService"/> and
/// retries the original request once. A per-handler semaphore collapses
/// concurrent 401s into one refresh so several parallel calls don't each rotate
/// the refresh token (rotation is single-use).
/// </summary>
/// <remarks>
/// The refresh call itself goes through <see cref="AuthService"/>'s own
/// HttpClient (not this handler), so there is no recursion.
/// </remarks>
public class AuthMessageHandler : DelegatingHandler
{
    private readonly AuthService _auth;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public AuthMessageHandler(AuthService auth)
    {
        _auth = auth;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        // Only attempt a refresh on a genuine auth failure (not on the refresh
        // endpoint itself, to avoid loops, and not for unauthenticated routes).
        if (response.StatusCode != HttpStatusCode.Unauthorized ||
            IsAuthEndpoint(request.RequestUri))
        {
            return response;
        }

        // Collapse concurrent 401s into a single refresh.
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var user = await _auth.RefreshAndGetCurrentUserAsync();
            if (user is null)
            {
                // Refresh failed (revoked/expired): immediately clear stale
                // client auth state and leave the 401 for the caller.
                _auth.ApplyRefreshedUser(null);
                return response;
            }

            // Reflect the refreshed identity for any subscribers (e.g. NavMenu).
            _auth.ApplyRefreshedUser(user);
        }
        finally
        {
            _refreshGate.Release();
        }

        // Retry the original request once with the freshly rotated cookie.
        // The request body may have been consumed, so clone it.
        var retry = await CloneAsync(request, cancellationToken);
        response.Dispose();
        return await base.SendAsync(retry, cancellationToken);
    }

    private static bool IsAuthEndpoint(Uri? uri)
        => uri is not null &&
           uri.AbsolutePath.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase);

    private static async Task<HttpRequestMessage> CloneAsync(
        HttpRequestMessage original, CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version
        };

        if (original.Content is not null)
        {
            var bytes = await original.Content.ReadAsByteArrayAsync(cancellationToken);
            clone.Content = new ByteArrayContent(bytes);
            foreach (var h in original.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
        }

        foreach (var h in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }

        return clone;
    }
}
