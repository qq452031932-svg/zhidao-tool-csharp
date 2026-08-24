using System.Text;
using System.IO;

namespace BaiduBatchTransfer.App.Features.Acceptance;

public interface IBaiduAcceptanceProbeOutputStore
{
    Task EnsureWritableAsync(CancellationToken cancellationToken = default);
    Task<string> SaveAsync(IReadOnlyList<BaiduAcceptanceEvidence> evidence, CancellationToken cancellationToken = default);
}

public sealed class BaiduAcceptanceProbeOutputStore : IBaiduAcceptanceProbeOutputStore
{
    private readonly string outputDirectory;

    public BaiduAcceptanceProbeOutputStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        outputDirectory = Path.Combine(Path.GetFullPath(dataRoot), "acceptance-evidence");
    }

    public async Task<string> SaveAsync(
        IReadOnlyList<BaiduAcceptanceEvidence> evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(
            outputDirectory,
            $"baidu-field-evidence-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(
            path,
            BaiduAcceptanceEvidenceDocument.Serialize(evidence),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);
        return path;
    }

    public async Task EnsureWritableAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var probePath = Path.Combine(outputDirectory, $".write-check-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(probePath, string.Empty, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(probePath); }
            catch (IOException) { }
        }
    }
}
