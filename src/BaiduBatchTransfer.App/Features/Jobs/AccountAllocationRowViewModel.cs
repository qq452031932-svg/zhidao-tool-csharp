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
    public int LockedItemCount { get; private set; }
    public int ExclusiveCandidateCount { get; private set; }
    public int PublicCandidateCount { get; private set; }
    public int UnusedExclusiveCandidateCount { get; private set; }
    public int OriginalTargetSuccessCount { get; private set; }
    public int EffectiveTargetSuccessCount { get; private set; }
    public int ReachableTargetSuccessCount { get; private set; }
    public int FulfilledCount { get; private set; }
    public int ReservedSlotCount { get; private set; }
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

    internal void ApplyConfirmation(WizardAccountConfirmation value)
    {
        OriginalTargetSuccessCount = value.OriginalTarget;
        EffectiveTargetSuccessCount = value.EffectiveTarget;
        ReachableTargetSuccessCount = value.ReachableTarget;
        FulfilledCount = value.Fulfilled;
        ReservedSlotCount = value.Reserved;
        LockedItemCount = value.Locked;
        ExclusiveCandidateCount = value.Exclusive;
        PublicCandidateCount = value.Public;
        UnusedExclusiveCandidateCount = value.Unused;
        OnPropertyChanged(string.Empty);
    }
}
