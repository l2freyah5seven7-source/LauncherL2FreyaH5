namespace L2Launcher.Models;

public sealed class LauncherSettings
{
    public string ClientDirectory { get; set; } = "Client";
    public string ClientDownloadUrl { get; set; } = "https://www.lineage2.org.uk/?wpdmdl=126";
    public string GitHubOwner { get; set; } = "REPLACE_WITH_OWNER";
    public string GitHubRepository { get; set; } = "REPLACE_WITH_REPOSITORY";
}

public sealed class UpdateProgress
{
    public string Message { get; init; } = string.Empty;
    public double Percent { get; init; }
}

public sealed class ClientInstallProgress
{
    public string Message { get; init; } = string.Empty;
    public double Percent { get; init; }
}
