namespace UpkiClient;

public sealed class AppSettings
{
    public string EndpointUrl { get; set; } = "";
    public string ClientCertPath { get; set; } = "";
    public string ClientCertPassword { get; set; } = "";
    public string ExpectedDnsIdentity { get; set; } = "";
    public bool   ValidateServerCertChain { get; set; } = true;
    public string TrustCustomCaPem { get; set; } = "";
}
