using System.Windows;

namespace BaiduBatchTransfer.App.Features.Common;

/// <summary>
/// 把 DataContext 存进资源，供不在可视树上的元素取用。
///
/// <para>
/// WPF 里 <c>DataGridColumn</c> 不是 FrameworkElement，不继承 DataContext，
/// 它的 Header 与 CellTemplate 也不挂在可视树上。于是
/// <c>{RelativeSource AncestorType=DataGrid}</c> 顺着树往上找不到 DataGrid，
/// 绑定静默失败：Content 变空、Command 变 null，点了没反应，还不报错。
/// </para>
///
/// <para>
/// 继承 <see cref="Freezable"/> 是关键：Freezable 即使放在资源字典里，
/// 也能继承所在元素的 DataContext，普通对象做不到。
/// </para>
///
/// <para>
/// 试过 <c>{x:Reference}</c> 直接指向 DataGrid，会触发循环依赖：
/// 解析列头需要 DataGrid 实例，而 DataGrid 要先解析完列头才能实例化。
/// </para>
///
/// 用法（引用方必须写上 assembly，否则 XamlReader.Parse 会到调用方程序集里找）：
/// <code>
/// xmlns:common="clr-namespace:BaiduBatchTransfer.App.Features.Common;assembly=知道综合工具"
/// &lt;DataGrid.Resources&gt;
///     &lt;common:BindingProxy x:Key="Vm" Data="{Binding}" /&gt;
/// &lt;/DataGrid.Resources&gt;
/// ...
/// Command="{Binding Data.SortCommand, Source={StaticResource Vm}}"
/// </code>
/// </summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy),
            new UIPropertyMetadata(null));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
