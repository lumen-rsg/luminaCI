using Microsoft.JSInterop;

namespace Lumina.WebApp.Services;

public class ThemeService
{
    private readonly IJSRuntime _jsRuntime;
    private string _currentTheme = "dark";

    public event Action? OnThemeChanged;
    public string CurrentTheme => _currentTheme;
    public bool IsDark => _currentTheme == "dark";

    public ThemeService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    public async Task InitializeAsync()
    {
        var saved = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", "lumina_theme");
        if (saved == "light" || saved == "dark")
        {
            _currentTheme = saved;
        }
        await ApplyThemeAsync();
    }

    public async Task ToggleThemeAsync()
    {
        _currentTheme = _currentTheme == "dark" ? "light" : "dark";
        await _jsRuntime.InvokeVoidAsync("localStorage.setItem", "lumina_theme", _currentTheme);
        await ApplyThemeAsync();
        OnThemeChanged?.Invoke();
    }

    private async Task ApplyThemeAsync()
    {
        await _jsRuntime.InvokeVoidAsync("setLuminaTheme", _currentTheme);
    }
}