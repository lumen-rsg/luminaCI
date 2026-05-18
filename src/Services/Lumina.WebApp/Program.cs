global using Lumina.WebApp;

using Lumina.WebApp.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Auth services (AuthService creates its own HttpClient internally — no circular dep)
builder.Services.AddScoped<AuthService>();

// Main HttpClient with auth handler for API calls
builder.Services.AddScoped<AuthMessageHandler>();
builder.Services.AddScoped(sp =>
{
    var auth = sp.GetRequiredService<AuthService>();
    var handler = new AuthMessageHandler(auth)
    {
        InnerHandler = new HttpClientHandler()
    };
    return new HttpClient(handler)
    {
        BaseAddress = new Uri(builder.HostEnvironment.BaseAddress)
    };
});

builder.Services.AddScoped<LuminaApiService>();

var host = builder.Build();

// Initialize auth state from localStorage
var auth = host.Services.GetRequiredService<AuthService>();
await auth.InitializeAsync();

await host.RunAsync();