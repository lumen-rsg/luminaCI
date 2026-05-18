namespace Lumina.WebApp.Services;

public class AuthMessageHandler : DelegatingHandler
{
    private readonly AuthService _auth;

    public AuthMessageHandler(AuthService auth)
    {
        _auth = auth;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_auth.Token))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _auth.Token);
        }
        return base.SendAsync(request, cancellationToken);
    }
}