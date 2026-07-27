using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;

namespace Lumina.WebApp.Services;

/// <summary>
/// Browser-side auth state.
///
/// The bearer JWT lives entirely inside an HttpOnly, Secure, SameSite=Strict
/// cookie set by the gateway on login/refresh. This service never touches the
/// raw token (it cannot — HttpOnly cookies are invisible to JS), so an XSS in
/// the rendered UI cannot exfiltrate it (SEC-05).
///
/// Auth state is derived from the server: <see cref="InitializeAsync"/> calls
/// <c>GET /api/auth/me</c>; when the short-lived access cookie has expired it
/// transparently rotates via <c>POST /api/auth/refresh</c> (the refresh cookie
/// is scoped to <c>/api/auth</c> and is sent automatically by the browser for
/// same-origin requests).
/// </summary>
public class AuthService
{
    private readonly HttpClient _http;
    private bool _initialized;

    public event Action? OnAuthStateChanged;

    public bool IsAuthenticated { get; private set; }
    public string? Username { get; private set; }
    public string? Role { get; private set; }

    public AuthService(NavigationManager nav)
    {
        // Dedicated HttpClient for the auth endpoints. In Blazor WASM, HttpClient
        // is backed by the browser's fetch, which attaches same-origin cookies
        // (the HttpOnly auth cookies) automatically — no manual cookie handling,
        // and no Authorization header is ever set in JS. This client is
        // intentionally NOT wrapped by AuthMessageHandler, otherwise /refresh
        // would recurse into itself.
        _http = new HttpClient { BaseAddress = new Uri(nav.BaseUri) };
    }

    /// <summary>
    /// Resolves the initial auth state on app startup. Tries /me first; on 401
    /// attempts a single /refresh and retries /me. Fires OnAuthStateChanged once.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;

        var me = await TryGetCurrentUserAsync();
        if (me is null)
        {
            // Access cookie may simply have expired — try refreshing it.
            await _http.PostAsync("/api/auth/refresh", content: null);
            me = await TryGetCurrentUserAsync();
        }

        ApplyUser(me);
        _initialized = true;
        OnAuthStateChanged?.Invoke();
    }

    /// <summary>
    /// Posts credentials. On success the gateway sets the auth cookies; this
    /// method records the returned username/role (no token in the body anymore).
    /// </summary>
    public async Task<LoginOutcome> LoginAsync(string username, string password)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("/api/auth/login", new { username, password });
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                return LoginOutcome.InvalidCredentials;
            if (!resp.IsSuccessStatusCode)
                return LoginOutcome.ServiceUnavailable;

            var result = await resp.Content.ReadFromJsonAsync<LoginResult>();
            ApplyUser(result is not null
                ? new CurrentUser(result.Username, result.Role)
                : null);
            OnAuthStateChanged?.Invoke();
            return IsAuthenticated ? LoginOutcome.Success : LoginOutcome.ServiceUnavailable;
        }
        catch (HttpRequestException)
        {
            return LoginOutcome.ServiceUnavailable;
        }
        catch (TaskCanceledException)
        {
            return LoginOutcome.ServiceUnavailable;
        }
        catch (JsonException)
        {
            return LoginOutcome.ServiceUnavailable;
        }
    }

    /// <summary>
    /// Logs out: tells the gateway to revoke the refresh token and clear the
    /// cookies, then resets local state.
    /// </summary>
    public async Task LogoutAsync()
    {
        try { await _http.PostAsync("/api/auth/logout", content: null); }
        catch { /* best-effort — clear local state regardless */ }
        ApplyUser(null);
        OnAuthStateChanged?.Invoke();
    }

    /// <summary>
    /// Used by AuthMessageHandler after a 401 to obtain a fresh access cookie.
    /// Returns the new current user (or null when the refresh failed).
    /// </summary>
    internal async Task<CurrentUser?> RefreshAndGetCurrentUserAsync()
    {
        try
        {
            var refreshResp = await _http.PostAsync("/api/auth/refresh", content: null);
            if (!refreshResp.IsSuccessStatusCode) return null;
            var result = await refreshResp.Content.ReadFromJsonAsync<LoginResult>();
            return result is null ? null : new CurrentUser(result.Username, result.Role);
        }
        catch
        {
            return null;
        }
    }

    private async Task<CurrentUser?> TryGetCurrentUserAsync()
    {
        try
        {
            var resp = await _http.GetAsync("/api/auth/me");
            if (resp.StatusCode == HttpStatusCode.Unauthorized) return null;
            if (!resp.IsSuccessStatusCode) return null;
            var result = await resp.Content.ReadFromJsonAsync<LoginResult>();
            return result is null ? null : new CurrentUser(result.Username, result.Role);
        }
        catch
        {
            return null;
        }
    }

    private void ApplyUser(CurrentUser? user)
    {
        if (user is null || string.IsNullOrEmpty(user.Username))
        {
            IsAuthenticated = false;
            Username = null;
            Role = null;
        }
        else
        {
            IsAuthenticated = true;
            Username = user.Username;
            Role = user.Role;
        }
    }

    /// <summary>
    /// Reflects a successful refresh performed elsewhere (e.g. by the HTTP
    /// handler) into the observable auth state and notifies subscribers.
    /// </summary>
    internal void ApplyRefreshedUser(CurrentUser? user)
    {
        ApplyUser(user);
        OnAuthStateChanged?.Invoke();
    }

    public enum LoginOutcome
    {
        Success,
        InvalidCredentials,
        ServiceUnavailable
    }

    // Internal so AuthMessageHandler can reference it as the refresh result.
    internal record CurrentUser(string? Username, string? Role);

    // The gateway returns { username, role, expires } from login/refresh and
    // { username, role } from /me — this record covers both.
    private record LoginResult(string? Username, string? Role, DateTime? Expires);
}
