using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Lumina.WebApp.Services;

public class AuthService
{
    private readonly HttpClient _http;
    private readonly IJSRuntime _js;
    private string? _cachedToken;
    private bool _initialized;

    public event Action? OnAuthStateChanged;

    public bool IsAuthenticated => !string.IsNullOrEmpty(_cachedToken);
    public string? Token => _cachedToken;

    public AuthService(NavigationManager nav, IJSRuntime js)
    {
        _http = new HttpClient { BaseAddress = new Uri(nav.BaseUri) };
        _js = js;
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        try
        {
            _cachedToken = await _js.InvokeAsync<string?>("localStorage.getItem", "lumina_token");
        }
        catch { /* JS not ready yet */ }
        _initialized = true;
        OnAuthStateChanged?.Invoke();
    }

    public async Task<bool> LoginAsync(string username, string password)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("/api/auth/login", new { username, password });
            if (resp.IsSuccessStatusCode)
            {
                var result = await resp.Content.ReadFromJsonAsync<LoginResult>();
                if (result?.Token != null)
                {
                    _cachedToken = result.Token;
                    await _js.InvokeVoidAsync("localStorage.setItem", "lumina_token", _cachedToken);
                    OnAuthStateChanged?.Invoke();
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public async Task LogoutAsync()
    {
        _cachedToken = null;
        try { await _js.InvokeVoidAsync("localStorage.removeItem", "lumina_token"); } catch { }
        OnAuthStateChanged?.Invoke();
    }

    private record LoginResult(string Token, DateTime Expires);
}