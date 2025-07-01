using Gun.Models.Responses;
using Gun.Options;
using Microsoft.Extensions.Options;
using System.Reflection;
using System.Text.Json;

namespace Gun.Clients;


public interface ILoginClient
{
    public Task<LoginTokenResponse?> GetTokenAsync(string? refreshToken);
}

public class LoginClient : ILoginClient
{
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<LoginClientOptions> _loginClientOptionsDelegate;

    public LoginClient(HttpClient httpClient, IOptionsMonitor<LoginClientOptions> loginClientOptionsDelegate)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _loginClientOptionsDelegate = loginClientOptionsDelegate ?? throw new ArgumentNullException(nameof(loginClientOptionsDelegate));
    }


    public async Task<LoginTokenResponse?> GetTokenAsync(string? refreshToken)
    {
        var formData = new Dictionary<string, string>()
        {
            { "client_id", _loginClientOptionsDelegate.CurrentValue.ClientId },
            { "redirect_uri", _loginClientOptionsDelegate.CurrentValue.RedirectUri },
            { "scope", _loginClientOptionsDelegate.CurrentValue.Scope },
            { "grant_type", _loginClientOptionsDelegate.CurrentValue.GrantType },
            { "client_info", _loginClientOptionsDelegate.CurrentValue.ClientInfo },
            { "x-client-SKU", _loginClientOptionsDelegate.CurrentValue.XClientSku },
            { "x-client-VER", _loginClientOptionsDelegate.CurrentValue.XClientVer },
            { "x-ms-lib-capability", _loginClientOptionsDelegate.CurrentValue.XMsLibCapability },
            { "x-client-current-telemetry", _loginClientOptionsDelegate.CurrentValue.XClientCurrentTelemetry },
            { "x-client-last-telemetry", _loginClientOptionsDelegate.CurrentValue.XClientLastTelemetry },
            { "refresh_token", refreshToken ?? _loginClientOptionsDelegate.CurrentValue.RefreshToken },
            { "claims", _loginClientOptionsDelegate.CurrentValue.Claims },
            { "X-AnchorMailbox", _loginClientOptionsDelegate.CurrentValue.XAnchorMailbox }
        };

        string requestUri = $"common/oauth2/v2.0/token?client-request-id={_loginClientOptionsDelegate.CurrentValue.ClientRequestId}";
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        request.Content = new FormUrlEncodedContent(formData);

        HttpResponseMessage response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var tokenResponse = JsonSerializer.Deserialize<LoginTokenResponse>(json);

        return tokenResponse;
    }
}
