using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using AvaloniaEdit.TextMate;
using QueueJanne.Core.Bodies;
using TextMateSharp.Grammars;

namespace QueueJanne.App.Views;

/// <summary>Read-only message body with JSON and XML syntax colouring that follows the app theme.</summary>
public partial class BodyViewer : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<BodyViewer, string?>(nameof(Text));

    public static readonly StyledProperty<BodyFormat> FormatProperty =
        AvaloniaProperty.Register<BodyViewer, BodyFormat>(nameof(Format));

    // Reading the grammar index and the themes is slow, so every viewer shares them.
    private static readonly RegistryOptions Registry = new(ThemeName.LightPlus);
    private static readonly Lazy<TextMateSharp.Themes.IRawTheme> LightTheme = new(() => Registry.LoadTheme(ThemeName.LightPlus));
    private static readonly Lazy<TextMateSharp.Themes.IRawTheme> DarkTheme = new(() => Registry.LoadTheme(ThemeName.DarkPlus));
    private static readonly Lazy<string> JsonScope = new(() => Registry.GetScopeByLanguageId(Registry.GetLanguageByExtension(".json").Id));
    private static readonly Lazy<string> XmlScope = new(() => Registry.GetScopeByLanguageId(Registry.GetLanguageByExtension(".xml").Id));

    private TextMate.Installation? _textMate;
    private string? _scope;
    private bool? _dark;

    public BodyViewer()
    {
        InitializeComponent();
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        ActualThemeVariantChanged += (_, _) => ApplyTheme();
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public BodyFormat Format
    {
        get => GetValue(FormatProperty);
        set => SetValue(FormatProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            Editor.Text = Text ?? string.Empty;
            Editor.ScrollToHome();
        }
        else if (change.Property == FormatProperty)
        {
            ApplyGrammar();
        }
    }

    // The installation stays once made: plain text and hex just clear the grammar, so moving between a JSON and a text
    // message doesn't rebuild the colouring.
    private void ApplyGrammar()
    {
        var scope = Format switch
        {
            BodyFormat.Json => JsonScope.Value,
            BodyFormat.Xml => XmlScope.Value,
            _ => null,
        };

        if (scope == _scope)
        {
            return;
        }

        if (_textMate is null)
        {
            if (scope is null)
            {
                return;
            }

            _textMate = Editor.InstallTextMate(Registry);
            ApplyTheme();
        }

        _scope = scope;
        _textMate.SetGrammar(scope);
    }

    private void ApplyTheme()
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        if (_textMate is null || dark == _dark)
        {
            return;
        }

        _dark = dark;
        _textMate.SetTheme(dark ? DarkTheme.Value : LightTheme.Value);
    }
}
