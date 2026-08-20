using CommunityToolkit.Mvvm.ComponentModel;
using BaiduBatchTransfer.Application.Jobs;

namespace BaiduBatchTransfer.App.Features.Jobs;

public sealed class AccountAllocationRowViewModel : ObservableObject
{
    private bool isSelected;
    private bool shareEnabled;
    private int targetItemCount;
    private int targetSuccessCount;
    private string defaultTargetPath;

    public AccountAllocationRowViewModel(WizardAccountOption option, string initialTargetPath)
    {
        if (option.SelectionDecision != BatchAccountSelectionDecision.Selected &&
            option.SelectionDecision != BatchAccountSelectionDecision.NotSelected)
            throw new ArgumentException("每个候选账号都必须明确选择或不选择。", nameof(option));
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
        isSelected = option.IsSelected;
        shareEnabled = option.ShareEnabled;
        PlannedTransferCount = option.PlannedTransferCount;
        PlannedShareCount = option.PlannedShareCount;
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
    public long PlannedTransferCount { get; private set; }
    public long PlannedShareCount { get; private set; }
    public long ConfirmedTransferCount { get; private set; }
    public long ConfirmedShareCount { get; private set; }

    public bool IsSelected { get => isSelected; set => SetProperty(ref isSelected, value); }
    public bool ShareEnabled { get => shareEnabled; set => SetProperty(ref shareEnabled, value); }
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

    public void ApplyConfirmation(WizardAccountConfirmation value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.AccountId != AccountId)
            throw new ArgumentException("账号确认结果与当前账号不匹配。", nameof(value));
        if (value.ShareEnabled != ShareEnabled)
            throw new ArgumentException("账号分享设置与当前草稿不匹配。", nameof(value));
        ValidateCount(value.PlannedTransferCount, nameof(value.PlannedTransferCount));
        ValidateCount(value.PlannedShareCount, nameof(value.PlannedShareCount));
        ValidateCount(value.ConfirmedTransferCount, nameof(value.ConfirmedTransferCount));
        ValidateCount(value.ConfirmedShareCount, nameof(value.ConfirmedShareCount));
        OriginalTargetSuccessCount = value.OriginalTarget;
        EffectiveTargetSuccessCount = value.EffectiveTarget;
        ReachableTargetSuccessCount = value.ReachableTarget;
        FulfilledCount = value.Fulfilled;
        ReservedSlotCount = value.Reserved;
        LockedItemCount = value.Locked;
        ExclusiveCandidateCount = value.Exclusive;
        PublicCandidateCount = value.Public;
        UnusedExclusiveCandidateCount = value.Unused;
        if (value.PlannedTransferCount is { } plannedTransfer) PlannedTransferCount = plannedTransfer;
        if (value.PlannedShareCount is { } plannedShare) PlannedShareCount = plannedShare;
        if (value.ConfirmedTransferCount is { } confirmedTransfer) ConfirmedTransferCount = confirmedTransfer;
        if (value.ConfirmedShareCount is { } confirmedShare) ConfirmedShareCount = confirmedShare;
        OnPropertyChanged(string.Empty);
    }

    public void ClearConfirmation()
    {
        PlannedTransferCount = 0;
        PlannedShareCount = 0;
        ConfirmedTransferCount = 0;
        ConfirmedShareCount = 0;
        OnPropertyChanged(nameof(PlannedTransferCount));
        OnPropertyChanged(nameof(PlannedShareCount));
        OnPropertyChanged(nameof(ConfirmedTransferCount));
        OnPropertyChanged(nameof(ConfirmedShareCount));
    }

    private static void ValidateCount(long? value, string parameterName)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(parameterName);
    }
}
