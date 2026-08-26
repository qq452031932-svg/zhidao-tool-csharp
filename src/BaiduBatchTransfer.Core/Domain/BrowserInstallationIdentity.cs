namespace BaiduBatchTransfer.Core.Domain;

public sealed class BrowserInstallationIdentity
{
    public BrowserInstallationIdentity(
        Guid installationId,
        BrowserFamily browserFamily,
        string installationDirectory,
        string executablePath,
        string publisher,
        string productName,
        string fileIdentity)
    {
        if (installationId == Guid.Empty)
            throw new ArgumentException("An installation ID is required.", nameof(installationId));
        if (browserFamily is not (BrowserFamily.Chrome or BrowserFamily.Edge))
            throw new ArgumentOutOfRangeException(nameof(browserFamily), browserFamily, "A managed browser family is required.");

        InstallationId = installationId;
        BrowserFamily = browserFamily;
        InstallationDirectory = RequireCanonicalAbsolutePath(installationDirectory, nameof(installationDirectory));
        ExecutablePath = RequireCanonicalAbsolutePath(executablePath, nameof(executablePath));
        Publisher = RequireText(publisher, nameof(publisher));
        ProductName = RequireText(productName, nameof(productName));
        FileIdentity = RequireText(fileIdentity, nameof(fileIdentity));

        var executableName = Path.GetFileName(ExecutablePath);
        var expectedExecutableName = browserFamily == BrowserFamily.Chrome ? "chrome.exe" : "msedge.exe";
        var expectedPublisher = browserFamily == BrowserFamily.Chrome ? "Google LLC" : "Microsoft Corporation";
        var expectedProduct = browserFamily == BrowserFamily.Chrome ? "Google Chrome" : "Microsoft Edge";
        if (!string.Equals(executableName, expectedExecutableName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Publisher, expectedPublisher, StringComparison.Ordinal) ||
            !string.Equals(ProductName, expectedProduct, StringComparison.Ordinal))
        {
            throw new ArgumentException("The browser installation identity does not match its family.");
        }

        var executableParent = Path.GetDirectoryName(ExecutablePath);
        if (!string.Equals(executableParent, InstallationDirectory, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The browser executable must be directly inside its installation directory.", nameof(executablePath));
    }

    public Guid InstallationId { get; }

    public BrowserFamily BrowserFamily { get; }

    public string InstallationDirectory { get; }

    public string ExecutablePath { get; }

    public string Publisher { get; }

    public string ProductName { get; }

    public string FileIdentity { get; }

    public override string ToString() =>
        "BrowserInstallationIdentity { InstallationId = [REDACTED], BrowserFamily = " +
        $"{BrowserFamily}, InstallationDirectory = [REDACTED], ExecutablePath = [REDACTED], " +
        "Publisher = [REDACTED], ProductName = [REDACTED], FileIdentity = [REDACTED] }";

    private static string RequireText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var trimmed = value.Trim();
        if (trimmed.Length > 256 || trimmed.Any(char.IsControl))
            throw new ArgumentException("The identity value is invalid or too long.", parameterName);
        return trimmed;
    }

    private static string RequireCanonicalAbsolutePath(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value != value.Trim() || value.Any(char.IsControl) || Path.EndsInDirectorySeparator(value))
            throw new ArgumentException("A canonical absolute path is required.", parameterName);
        if (!Path.IsPathFullyQualified(value))
            throw new ArgumentException("A canonical absolute path is required.", parameterName);

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(value);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new ArgumentException("A canonical absolute path is required.", parameterName, exception);
        }

        var trimmedValue = Path.TrimEndingDirectorySeparator(value);
        var trimmedFullPath = Path.TrimEndingDirectorySeparator(fullPath);
        if (!string.Equals(trimmedValue, trimmedFullPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A canonical absolute path is required.", parameterName);

        return fullPath;
    }
}
