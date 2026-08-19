using CommunityToolkit.Mvvm.ComponentModel;

namespace BaiduBatchTransfer.App.Features.Jobs;

public sealed class AccountAllocationRowViewModel : ObservableObject
{
    private bool isSelected;
    private int targetItemCount;
    private int targetSuccessCount;
    private string defaultTargetPath;

    public AccountAllocationRowViewModel(WizardAccountOption option, string initialTargetPath)
    {
        AccountId = option.AccountId;
        DisplayName = option.DisplayName;
        StatusText = option.Status == "Active" ? "可用" : "不可用";
        CapacityTotal = option.CapacityTotal;
        CapacityUsed = option.CapacityUsed;
        LockedItemCount = option.LockedItemCount;
        ExclusiveCandidateCount = option.ExclusiveCandidateCount;
        PublicCandidateCount = option.PublicCandidateCount;
        UnusedExclusiveCandidateCount = option.UnusedExclusiveCandidateCount;
        OriginalTargetSuccessCount = option.OriginalTargetSuccessCount;
        EffectiveTargetSuccessCount = option.EffectiveTargetSuccessCount;
        ReachableTargetSuccessCount = option.ReachableTargetSuccessCount;
        FulfilledCount = option.FulfilledCount;
        ReservedSlotCount = option.ReservedSlotCount;
        targetSuccessCount = option.OriginalTargetSuccessCount;
        defaultTargetPath = initialTargetPath;
    }

    public Guid AccountId { get; }
    public string DisplayName { get; }
    public string StatusText { get; }
    public long? CapacityTotal { get; }
    public long? CapacityUsed { get; }
    public long? RemainingCapacity => CapacityTotal is null || CapacityUsed is null ? null : Math.Max(0, CapacityTotal.Value - CapacityUsed.Value);
    public int LockedItemCount { get; }
    public int ExclusiveCandidateCount { get; }
    public int PublicCandidateCount { get; }
    public int UnusedExclusiveCandidateCount { get; }
    public int OriginalTargetSuccessCount { get; }
    public int EffectiveTargetSuccessCount { get; }
    public int ReachableTargetSuccessCount { get; }
    public int FulfilledCount { get; }
    public int ReservedSlotCount { get; }
    public int ShortfallCount => Math.Max(0, TargetSuccessCount - ReachableTargetSuccessCount);
    public double AllocationPercentage { get; internal set; }

    public bool IsSelected { get => isSelected; set => SetProperty(ref isSelected, value); }
    public int TargetItemCount { get => targetItemCount; set => SetProperty(ref targetItemCount, Math.Max(0, value)); }
    public int TargetSuccessCount
    {
        get => targetSuccessCount;
        set
        {
            if (SetProperty(ref targetSuccessCount, Math.Max(0, value)))
                OnPropertyChanged(nameof(ShortfallCount));
        }
    }
    public string DefaultTargetPath { get => defaultTargetPath; set => SetProperty(ref defaultTargetPath, value ?? string.Empty); }
}
