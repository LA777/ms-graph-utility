namespace Gun.Options;

public class LoginClientOptions
{
    public string LoginBaseUrl { get; set; } = string.Empty;
    public int RetryAttempts { get; set; } = 3;
    public string ClientRequestId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string GrantType { get; set; } = string.Empty;
    public string ClientInfo { get; set; } = string.Empty;
    public string XClientSku { get; set; } = string.Empty;
    public string XClientVer { get; set; } = string.Empty;
    public string XMsLibCapability { get; set; } = string.Empty;
    public string XClientCurrentTelemetry { get; set; } = string.Empty;
    public string XClientLastTelemetry { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public string Claims { get; set; } = string.Empty;
    public string XAnchorMailbox { get; set; } = string.Empty;
    public string Origin { get; set; } = string.Empty;
}
