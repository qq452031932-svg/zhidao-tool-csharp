using System.Collections.ObjectModel;
using BaiduBatchTransfer.Application.Scheduling;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BaiduBatchTransfer.App.Features.Jobs;

public sealed record ExistingShareDecisionOption(ExistingShareDecision? Decision, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed class ExistingShareDecisionAccountRowViewModel : ObservableObject
{
    private ExistingShareDecisionOption selectedOverrideOption;

    internal ExistingShareDecisionAccountRowViewModel(
        ExistingShareAccountSummary summary,
        string accountLabel,
        ExistingShareDecisionOption followDefault)
    {
        Summary = summary ?? throw new ArgumentNullException(nameof(summary));
        AccountLabel = string.IsNullOrWhiteSpace(accountLabel)
            ? throw new ArgumentException("账号标签不能为空。", nameof(accountLabel))
            : accountLabel;
        selectedOverrideOption = followDefault ?? throw new ArgumentNullException(nameof(followDefault));
    }

    internal ExistingShareAccountSummary Summary { get; }
    public string AccountLabel { get; }
    public int DiscoveredCount => Summary.DiscoveredCount;
    public int CompatibleCount => Summary.CompatibleCount;
    public int IncompatibleCount => Summary.IncompatibleCount;
    public ExistingShareDecision? OverrideDecision => SelectedOverrideOption.Decision;

    public ExistingShareDecisionOption SelectedOverrideOption
    {
        get => selectedOverrideOption;
        set => SetProperty(ref selectedOverrideOption, value ?? throw new ArgumentNullException(nameof(value)));
    }

    public override string ToString() =>
        $"ExistingShareDecisionAccountRowViewModel {{ Account = {AccountLabel}, DiscoveredCount = {DiscoveredCount}, CompatibleCount = {CompatibleCount}, IncompatibleCount = {IncompatibleCount}, Decision = [REDACTED] }}";
}

public sealed class ExistingShareDecisionViewModel : ObservableObject
{
    private readonly ExistingShareBatchSummary summary;
    private ExistingShareDecisionOption selectedBatchDefaultOption;
    private string validationMessage = string.Empty;
    private bool canConfirm;

    public ExistingShareDecisionViewModel(ExistingShareBatchSummary summary)
    {
        this.summary = summary ?? throw new ArgumentNullException(nameof(summary));
        if (summary.BatchJobId == Guid.Empty || string.IsNullOrWhiteSpace(summary.ConfirmationToken))
            throw new ArgumentException("已有分享确认摘要无效。", nameof(summary));
        if (summary.Accounts.Count == 0 || summary.Accounts.Any(value => value.AccountId == Guid.Empty) ||
            summary.Accounts.Select(value => value.AccountId).Distinct().Count() != summary.Accounts.Count)
            throw new ArgumentException("已有分享确认账号摘要无效。", nameof(summary));

        BatchDecisionOptions =
        [
            new(ExistingShareDecision.Reuse, "复用已有分享"),
            new(ExistingShareDecision.Regenerate, "重新生成"),
            new(ExistingShareDecision.Cancel, "取消分享"),
        ];
        AccountOverrideOptions =
        [
            new(null, "跟随批次默认"),
            .. BatchDecisionOptions,
        ];
        selectedBatchDefaultOption = BatchDecisionOptions[0];
        Accounts = new ObservableCollection<ExistingShareDecisionAccountRowViewModel>(
            summary.Accounts.Select((value, index) => new ExistingShareDecisionAccountRowViewModel(
                value,
                string.IsNullOrWhiteSpace(value.AccountDisplayName) ? $"账号 {index + 1}" : value.AccountDisplayName,
                AccountOverrideOptions[0])));
        foreach (var account in Accounts)
            account.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(ExistingShareDecisionAccountRowViewModel.SelectedOverrideOption)) return;
                ConfirmedCommand = null;
                RefreshValidation();
            };
        RefreshValidation();
    }

    public IReadOnlyList<ExistingShareDecisionOption> BatchDecisionOptions { get; }
    public IReadOnlyList<ExistingShareDecisionOption> AccountOverrideOptions { get; }
    public ObservableCollection<ExistingShareDecisionAccountRowViewModel> Accounts { get; }
    public int TotalDiscoveredCount => Accounts.Sum(value => value.DiscoveredCount);
    public int TotalCompatibleCount => Accounts.Sum(value => value.CompatibleCount);
    public int TotalIncompatibleCount => Accounts.Sum(value => value.IncompatibleCount);
    public ExistingShareDecision BatchDefault => SelectedBatchDefaultOption.Decision!.Value;

    public ExistingShareDecisionOption SelectedBatchDefaultOption
    {
        get => selectedBatchDefaultOption;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Decision is null || !BatchDecisionOptions.Contains(value))
                throw new ArgumentException("批次默认决定无效。", nameof(value));
            if (!SetProperty(ref selectedBatchDefaultOption, value)) return;
            ConfirmedCommand = null;
            OnPropertyChanged(nameof(BatchDefault));
            OnPropertyChanged(nameof(DecisionSummaryText));
            RefreshValidation();
        }
    }

    public string DecisionSummaryText =>
        $"共发现 {TotalDiscoveredCount} 个已有分享，其中兼容 {TotalCompatibleCount} 个、不兼容 {TotalIncompatibleCount} 个；批次默认选择：{SelectedBatchDefaultOption.DisplayName}。";

    public string ValidationMessage
    {
        get => validationMessage;
        private set => SetProperty(ref validationMessage, value);
    }

    public bool CanConfirm
    {
        get => canConfirm;
        private set => SetProperty(ref canConfirm, value);
    }

    public ConfirmExistingShareDecisionCommand? ConfirmedCommand { get; private set; }

    public bool TryConfirm()
    {
        RefreshValidation();
        if (!CanConfirm)
        {
            ConfirmedCommand = null;
            return false;
        }

        ConfirmedCommand = new ConfirmExistingShareDecisionCommand(
            summary.BatchJobId,
            summary.ConfirmationToken,
            BatchDefault,
            Accounts.Where(value => value.OverrideDecision is not null)
                .ToDictionary(value => value.Summary.AccountId, value => value.OverrideDecision!.Value));
        return true;
    }

    private void RefreshValidation()
    {
        var invalid = Accounts.FirstOrDefault(value =>
            value.DiscoveredCount > 0 && value.CompatibleCount == 0 &&
            (value.OverrideDecision ?? BatchDefault) == ExistingShareDecision.Reuse);
        ValidationMessage = invalid is null
            ? string.Empty
            : $"{invalid.AccountLabel} 没有可复用的兼容分享，请选择重新生成或取消分享。";
        CanConfirm = invalid is null;
        OnPropertyChanged(nameof(DecisionSummaryText));
    }

    public override string ToString() =>
        $"ExistingShareDecisionViewModel {{ AccountCount = {Accounts.Count}, Counts = {TotalDiscoveredCount}/{TotalCompatibleCount}/{TotalIncompatibleCount}, Confirmation = [REDACTED] }}";
}
