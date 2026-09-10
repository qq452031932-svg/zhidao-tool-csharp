using System.Security.Cryptography;
using System.Text;
using SuperPan.Licensing.Client;

namespace SuperPan.Licensing.Client.Tests;

public sealed class MachineFingerprintProviderTests
{
    [Fact]
    public void MachineFingerprintNormalizesMachineGuidAndBiosUuid()
    {
        var first = new MachineFingerprintProvider(new StubSource(" {A0B1C2D3-E4F5-4678-9ABC-DEF012345678} ", " 00112233-4455-6677-8899-AABBCCDDEEFF "));
        var second = new MachineFingerprintProvider(new StubSource("a0b1c2d3-e4f5-4678-9abc-def012345678", "00112233-4455-6677-8899-aabbccddeeff"));

        var expectedInput = "SuperPan.Licensing.MachineFingerprint.v1\nmachine-guid:a0b1c2d3-e4f5-4678-9abc-def012345678\nbios-uuid:00112233-4455-6677-8899-aabbccddeeff";
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(expectedInput))).ToLowerInvariant();

        Assert.Equal(expected, first.GetMachineHash());
        Assert.Equal(expected, second.GetMachineHash());
    }

    [Theory]
    [InlineData("machine-guid", null)]
    [InlineData(null, "bios-uuid")]
    public void MachineFingerprintUsesOnlyTheAvailableStableField(string? machineGuid, string? biosUuid)
    {
        var provider = new MachineFingerprintProvider(new StubSource(machineGuid, biosUuid));

        var first = provider.GetMachineHash();
        var second = provider.GetMachineHash();

        Assert.Equal(64, first.Length);
        Assert.Equal(first, second);
    }

    [Fact]
    public void MachineFingerprintFailsClosedWhenBothFieldsAreMissingAndNeverCreatesRandomFallback()
    {
        var source = new StubSource(" ", null);
        var provider = new MachineFingerprintProvider(source);

        var first = Assert.Throws<MachineFingerprintException>(() => provider.GetMachineHash());
        var second = Assert.Throws<MachineFingerprintException>(() => provider.GetMachineHash());

        Assert.Equal(first.Message, second.Message);
        Assert.Equal(2, source.MachineGuidReads);
        Assert.Equal(2, source.BiosUuidReads);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF")]
    [InlineData("Default string")]
    [InlineData("System Serial Number")]
    [InlineData("Not Specified")]
    [InlineData("INVALID")]
    public void MachineFingerprintTreatsCommonFirmwareUuidSentinelsAsMissing(string sentinel)
    {
        var exception = Assert.Throws<MachineFingerprintException>(() =>
            new MachineFingerprintProvider(new StubSource(null, sentinel)).GetMachineHash());

        Assert.DoesNotContain(sentinel, exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MachineFingerprintFallsBackToMachineGuidWhenBiosUuidIsASentinel()
    {
        var withSentinel = new MachineFingerprintProvider(new StubSource("valid-machine-guid", "00000000-0000-0000-0000-000000000000"));
        var withoutBios = new MachineFingerprintProvider(new StubSource("valid-machine-guid", null));

        Assert.Equal(withoutBios.GetMachineHash(), withSentinel.GetMachineHash());
    }

    [Fact]
    public void MachineFingerprintDiagnosticsNeverExposeRawIdentifiersOrHash()
    {
        const string machineGuid = "machine-guid-secret";
        const string biosUuid = "bios-uuid-secret";
        var provider = new MachineFingerprintProvider(new StubSource(machineGuid, biosUuid));
        var hash = provider.GetMachineHash();

        Assert.DoesNotContain(machineGuid, provider.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(biosUuid, provider.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(hash, provider.ToString(), StringComparison.Ordinal);
    }

    private sealed class StubSource(string? machineGuid, string? biosUuid) : IMachineIdentitySource
    {
        public int MachineGuidReads { get; private set; }
        public int BiosUuidReads { get; private set; }

        public string? ReadMachineGuid()
        {
            MachineGuidReads++;
            return machineGuid;
        }

        public string? ReadBiosUuid()
        {
            BiosUuidReads++;
            return biosUuid;
        }
    }
}
