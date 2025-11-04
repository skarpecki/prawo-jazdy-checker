namespace UpkiClient;

public sealed class AppSettings
{
    public string WorkingDirectory { get; set; } = "";
    public string EndpointUrl { get; set; } = "";
    public int DelayLowerBoundMs { get; set; } = 500;
    public int DelayUpperBoundMs { get; set; } = 1000;
}
