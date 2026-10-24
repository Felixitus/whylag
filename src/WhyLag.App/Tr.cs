using System.Windows.Markup;
using WhyLag.Core.Localization;

namespace WhyLag.App;

/// <summary>XAML access to translations: <c>Text="{local:Tr ui.live}"</c>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class Tr(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
