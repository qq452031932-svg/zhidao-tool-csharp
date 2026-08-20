using System.Collections.ObjectModel;
using BaiduBatchTransfer.Core.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BaiduBatchTransfer.App.Features.Imports;

public sealed class ExcelColumnMappingViewModel : ObservableObject
{
    private readonly TabularWorkbookInspection inspection;
    private bool firstRowIsHeader;
    private string validationMessage = string.Empty;

    public ExcelColumnMappingViewModel(
        TabularWorkbookInspection inspection,
        ImportColumnMappingSuggestion suggestion)
    {
        this.inspection = inspection ?? throw new ArgumentNullException(nameof(inspection));
        ArgumentNullException.ThrowIfNull(suggestion);
        firstRowIsHeader = suggestion.HeaderRowNumber is not null;
        Columns = new ObservableCollection<ExcelColumnMappingChoice>(inspection.Columns.Select(column =>
        {
            var role = suggestion.Columns.FirstOrDefault(value =>
                value.SourceColumnIndex == column.SourceColumnIndex)?.Role ?? TabularImportColumnRole.Preserve;
            return new ExcelColumnMappingChoice(column.SourceColumnIndex, column.Header, role);
        }));
        foreach (var column in Columns) column.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(ExcelColumnMappingChoice.Role)) return;
            ConfirmedMapping = null;
            ValidationMessage = string.Empty;
        };
        PreviewRows = [];
        RebuildSnapshot();
    }

    public ObservableCollection<ExcelColumnMappingChoice> Columns { get; }

    public ObservableCollection<ExcelMappingPreviewRow> PreviewRows { get; }

    public bool FirstRowIsHeader
    {
        get => firstRowIsHeader;
        set
        {
            if (!SetProperty(ref firstRowIsHeader, value)) return;
            ConfirmedMapping = null;
            ValidationMessage = string.Empty;
            RebuildSnapshot();
        }
    }

    public string ValidationMessage
    {
        get => validationMessage;
        private set => SetProperty(ref validationMessage, value);
    }

    public ConfirmedImportColumnMapping? ConfirmedMapping { get; private set; }

    public bool TryConfirm()
    {
        try
        {
            var headerRow = FirstRowIsHeader
                ? inspection.HeaderRowNumber ?? inspection.SampleRows.FirstOrDefault()?.SourceRowNumber
                : null;
            ConfirmedMapping = new ConfirmedImportColumnMapping(
                headerRow,
                Columns.Select(column => new TabularImportColumnMapping(column.SourceColumnIndex, column.Role)),
                Columns.Select(column => new TabularInspectionColumn(column.SourceColumnIndex, column.SourceHeader)));
            ValidationMessage = string.Empty;
            return true;
        }
        catch (ArgumentException)
        {
            ConfirmedMapping = null;
            ValidationMessage = "必须且只能选择一列作为分享链接。";
            return false;
        }
    }

    private void RebuildSnapshot()
    {
        var firstObserved = inspection.ObservedRows.FirstOrDefault();
        foreach (var column in Columns)
        {
            var sourceHeader = FirstRowIsHeader && firstObserved is not null &&
                column.SourceColumnIndex < firstObserved.RawValues.Count
                ? firstObserved.RawValues[column.SourceColumnIndex]
                : FirstRowIsHeader
                    ? inspection.Columns.FirstOrDefault(value => value.SourceColumnIndex == column.SourceColumnIndex)?.Header
                    : $"第 {column.SourceColumnIndex + 1} 列";
            column.SetSourceHeader(sourceHeader);
        }
        PreviewRows.Clear();
        var rows = FirstRowIsHeader ? inspection.ObservedRows.Skip(1) : inspection.ObservedRows;
        foreach (var row in rows.Take(20))
            PreviewRows.Add(new ExcelMappingPreviewRow(row.SourceRowNumber, string.Join("　|　", row.RawValues)));
    }
}

public sealed class ExcelColumnMappingChoice : ObservableObject
{
    private TabularImportColumnRole role;
    private string header;

    public ExcelColumnMappingChoice(int sourceColumnIndex, string? header, TabularImportColumnRole role)
    {
        SourceColumnIndex = sourceColumnIndex;
        SourceHeader = header;
        this.header = string.IsNullOrEmpty(header) ? $"第 {sourceColumnIndex + 1} 列" : header;
        this.role = role;
    }

    public int SourceColumnIndex { get; }

    public string Header
    {
        get => header;
        private set => SetProperty(ref header, value);
    }

    public string? SourceHeader { get; private set; }

    public TabularImportColumnRole Role
    {
        get => role;
        set => SetProperty(ref role, value);
    }

    public IReadOnlyList<ExcelColumnRoleOption> RoleOptions => ExcelColumnRoleOption.All;

    internal void SetSourceHeader(string? value)
    {
        var snapshot = string.IsNullOrEmpty(value) ? $"第 {SourceColumnIndex + 1} 列" : value;
        SourceHeader = snapshot;
        Header = snapshot;
    }

    public override string ToString() =>
        $"ExcelColumnMappingChoice {{ SourceColumnIndex = {SourceColumnIndex}, Role = {Role}, Header = [REDACTED] }}";
}

public sealed record ExcelColumnRoleOption(TabularImportColumnRole Role, string DisplayName)
{
    public static IReadOnlyList<ExcelColumnRoleOption> All { get; } =
    [
        new(TabularImportColumnRole.Preserve, "原样保留"),
        new(TabularImportColumnRole.SourceTitle, "源标题"),
        new(TabularImportColumnRole.ShareUrl, "分享链接（必选）"),
        new(TabularImportColumnRole.ExtractionCode, "提取码"),
    ];
}

public sealed record ExcelMappingPreviewRow(int SourceRowNumber, string ValuesText)
{
    public override string ToString() =>
        $"ExcelMappingPreviewRow {{ SourceRowNumber = {SourceRowNumber}, UserContent = [REDACTED] }}";
}
