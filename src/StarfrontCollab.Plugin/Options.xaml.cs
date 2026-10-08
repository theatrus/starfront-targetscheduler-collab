using System.ComponentModel.Composition;
using System.Windows;

namespace StarfrontCollab.Plugin;

[Export(typeof(ResourceDictionary))]
public partial class Options : ResourceDictionary
{
    public Options() => InitializeComponent();
}
