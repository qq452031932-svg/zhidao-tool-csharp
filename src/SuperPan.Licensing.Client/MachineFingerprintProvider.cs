using System.Management;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace SuperPan.Licensing.Client;

public interface IMachineIdentitySource
{
    string? ReadMachineGuid();
    string? ReadBiosUuid();
}

public interface IMachineFingerprintProvider
{
    string GetMachineHash();
}

public sealed class MachineFingerprintException : Exception
{
    public MachineFingerprintException() : base("A stable device identifier is unavailable.") { }
}

public sealed class MachineFingerprintProvider : IMachineFingerprintProvider
{
    private const string FingerprintPurpose = "SuperPan.Licensing.MachineFingerprint.v1";
    private readonly IMachineIdentitySource source;

    public MachineFingerprintProvider(IMachineIdentitySource? source = null) =>
        this.source = source ?? WindowsMachineIdentitySource.Instance;

    public string GetMachineHash()
    {
        var machineGuid = ReadNormalized(source.ReadMachineGuid);
        var biosUuid = ReadNormalized(source.ReadBiosUuid);
        if (machineGuid is null && biosUuid is null)
        {
            throw new MachineFingerprintException();
        }

        var material = new StringBuilder(FingerprintPurpose);
        if (machineGuid is not null)
        {
            material.Append("\nmachine-guid:").Append(machineGuid);
        }
        if (biosUuid is not null)
        {
            material.Append("\nbios-uuid:").Append(biosUuid);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString()))).ToLowerInvariant();
    }

    public override string ToString() => nameof(MachineFingerprintProvider);

    private static string? ReadNormalized(Func<string?> read)
    {
        try
        {
            return Normalize(read());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return null;
        }
    }

    private static string? Normalize(string? value)
    {
        value = value?.Trim().Normalize(NormalizationForm.FormKC);
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("to be filled", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return Guid.TryParse(value, out var guid)
            ? guid.ToString("D")
            : value.ToLowerInvariant();
    }

    private sealed class WindowsMachineIdentitySource : IMachineIdentitySource
    {
        public static readonly WindowsMachineIdentitySource Instance = new();

        public string? ReadMachineGuid()
        {
            if (!OperatingSystem.IsWindows()) return null;
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", writable: false);
            return key?.GetValue("MachineGuid") as string;
        }

        public string? ReadBiosUuid()
        {
            if (!OperatingSystem.IsWindows()) return null;
            using var searcher = new ManagementObjectSearcher("SELECT UUID FROM Win32_ComputerSystemProduct");
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    return item["UUID"]?.ToString();
                }
            }
            return null;
        }
    }
}
