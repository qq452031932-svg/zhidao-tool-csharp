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
        PreviewRows = new ObservableCollection<ExcelMappingPreviewRow>(inspection.SampleRows.Select(row =>
            new ExcelMappingPreviewRow(row.SourceRowNumber, string.Join("　|　", row.RawValues))));
    }

    public ObservableCollection<ExcelColumnMappingChoice> Columns { get; }

    public ObservableCollection<ExcelMappingPreviewRow> PreviewRows { get; }

    public bool FirstRowIsHeader
    {
        get => firstRowIsHeader;
        set => SetProperty(ref firstRowIsHeader, value);
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
                Columns.Select(column => new TabularImportColumnMapping(column.SourceColumnIndex, column.Role)));
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
}

public sealed class ExcelColumnMappingChoice : ObservableObject
{
    private TabularImportColumnRole role;

    public ExcelColumnMappingChoice(int sourceColumnIndex, string? header, TabularImportColumnRole role)
    {
        SourceColumnIndex = sourceColumnIndex;
        Header = string.IsNullOrWhiteSpace(header) ? $"第 {sourceColumnIndex + 1} 列" : header;
        this.role = role;
    }

    public int SourceColumnIndex { get; }

    public string Header { get; }

    public TabularImportColumnRole Role
    {
        get => role;
        set => SetProperty(ref role, value);
    }

    public IReadOnlyList<ExcelColumnRoleOption> RoleOptions => ExcelColumnRoleOption.All;

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
