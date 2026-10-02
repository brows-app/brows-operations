using System.Windows;
using System.Windows.Markup;

[assembly: ComVisible(false)]
[assembly: InternalsVisibleTo("Brows.Operations.Windows.Tests")]

[assembly: XmlnsDefinition("http://schemas.brows.app/winfx/2026/xaml/presentation", "Brows.Windows")]
[assembly: XmlnsDefinition("http://schemas.brows.app/winfx/2026/xaml/presentation", "Brows.Windows.Controls")]
[assembly: XmlnsPrefix("http://schemas.brows.app/winfx/2026/xaml/presentation", "brows")]
[assembly: ThemeInfo(ResourceDictionaryLocation.None,
                     ResourceDictionaryLocation.SourceAssembly)]
