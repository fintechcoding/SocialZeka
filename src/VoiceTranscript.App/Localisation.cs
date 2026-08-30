using System.Windows.Markup;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.App;

/// <summary>
/// Puts a translated string into markup: <c>Text="{loc:T overview.title}"</c>.
///
/// A markup extension rather than a static resource lookup because the key has to be readable in
/// the markup. A screen full of <c>{StaticResource S_0147}</c> cannot be edited by anybody, and
/// the whole point of extracting the strings was to make the wording easier to work on, not
/// harder.
/// </summary>
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Localisation.T(Key);
}
