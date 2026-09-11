using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class AccountAllocationRowViewModelTests
{
    private const long Gibibyte = 1024L * 1024 * 1024;
    private const long Tebibyte = 1024L * Gibibyte;

    [Fact]
    public void CapacityPresentationUsesReadableGbAndTbValues()
    {
        var row = CreateRow(capacityTotal: 2 * Tebibyte, capacityUsed: 512 * Gibibyte);

        Assert.Equal("1.5 TB", row.RemainingCapacityText);
        Assert.Equal("512 GB", row.CapacityUsedText);
        Assert.Equal("已用 512 GB / 2 TB", row.CapacitySummaryText);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(1024L, null)]
    public void CapacityPresentationUsesExplicitUnknownForIncompleteData(long? capacityTotal, long? capacityUsed)
    {
        var row = CreateRow(capacityTotal, capacityUsed);

        Assert.Equal("未知", row.RemainingCapacityText);
        Assert.Equal("未知", row.CapacityUsedText);
        Assert.Equal("容量未知", row.CapacitySummaryText);
    }

    [Fact]
    public void RefreshedCapacityUpdatesAllDependentPresentation()
    {
        var row = CreateRow(null, null);
        var changed = new List<string?>();
        row.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        row.ApplyCapacity(new AccountCapacity(2 * Tebibyte, 512 * Gibibyte));

        Assert.Equal(2 * Tebibyte, row.CapacityTotal);
        Assert.Equal(512 * Gibibyte, row.CapacityUsed);
        Assert.Equal("已用 512 GB / 2 TB", row.CapacitySummaryText);
        Assert.Contains(nameof(row.CapacityTotal), changed);
        Assert.Contains(nameof(row.CapacityUsed), changed);
        Assert.Contains(nameof(row.RemainingCapacity), changed);
        Assert.Contains(nameof(row.CapacitySummaryText), changed);
    }

    [Fact]
    public void RefreshedAvatarUpdatesTheAccountCardSource()
    {
        var row = CreateRow(null, null);
        var changed = new List<string?>();
        row.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        var avatar = new Uri("https://himg.bdimg.com/sys/portrait/item/test.jpg");

        row.ApplyAvatar(avatar);

        Assert.Equal(avatar, row.AvatarUri);
        Assert.Contains(nameof(row.AvatarUri), changed);
    }

    private static AccountAllocationRowViewModel CreateRow(long? capacityTotal, long? capacityUsed)
    {
        var option = new WizardAccountOption(
            Guid.NewGuid(),
            "测试账号",
            "Active",
            IsEnabled: true,
            IsDeleted: false,
            capacityTotal,
            capacityUsed,
            LockedItemCount: 0,
            ExclusiveCandidateCount: 0,
            PublicCandidateCount: 0,
            UnusedExclusiveCandidateCount: 0,
            OriginalTargetSuccessCount: 0,
            EffectiveTargetSuccessCount: 0,
            ReachableTargetSuccessCount: 0,
            FulfilledCount: 0,
            ReservedSlotCount: 0,
            BatchAccountSelectionDecision.NotSelected);
        return new AccountAllocationRowViewModel(option, "/目标");
    }
}
