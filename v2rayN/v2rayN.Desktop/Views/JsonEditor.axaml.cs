using System.Text.Json;
using System.Xml;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

public partial class JsonEditor : UserControl
{
    private static readonly JsonSerializerOptions SIndentedOptions = new() { WriteIndented = true };
    private static readonly JsonDocumentOptions SDocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static readonly Lazy<IHighlightingDefinition> SHighlightingDark =
        new(() => BuildHighlighting(dark: true), isThreadSafe: true);

    private static readonly Lazy<IHighlightingDefinition> SHighlightingLight =
        new(() => BuildHighlighting(dark: false), isThreadSafe: true);

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<JsonEditor, string>(nameof(Text), defaultValue: string.Empty);

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
    public bool IsReadOnly
    {
        get => Editor.IsReadOnly;
        set
        {
            Editor.IsReadOnly = value;
            btnFormat.IsVisible = !value;
            menuFormat.IsEnabled = menuPaste.IsEnabled = !value;
        }
    }
    public bool ShowValidation { get => txtValidation.IsVisible; set => txtValidation.IsVisible = value; }

    public JsonEditor()
    {
        InitializeComponent();

        void UpdateHighlighting()
        {
            var theme = ActualThemeVariant ?? Application.Current?.ActualThemeVariant ?? ThemeVariant.Light;
            while (theme.InheritVariant != null) theme = theme.InheritVariant;
            Editor.SyntaxHighlighting = theme == ThemeVariant.Dark ? SHighlightingDark.Value : SHighlightingLight.Value;
        }
        UpdateHighlighting();
        ActualThemeVariantChanged += (_, _) => UpdateHighlighting();
        Editor.TextArea.TextView.Options.EnableHyperlinks = false;

        Editor.TextChanged += (_, _) =>
        {
            try
            {
                using var parsed = JsonDocument.Parse(Editor.Text, SDocumentOptions);
                txtValidation.Text = WorkspaceText.T("JSON 格式正确", "Valid JSON");
                txtValidation.Foreground = Avalonia.Media.Brushes.ForestGreen;
            }
            catch (JsonException error)
            {
                txtValidation.Text = string.IsNullOrWhiteSpace(Editor.Text) ? "—" : WorkspaceText.T("格式错误，行 ", "Invalid JSON at line ") + (error.LineNumber + 1);
                if (string.IsNullOrWhiteSpace(Editor.Text)) txtValidation.ClearValue(TextBlock.ForegroundProperty);
                else txtValidation.Foreground = Avalonia.Media.Brushes.IndianRed;
            }
            if (Text != Editor.Text)
            {
                SetCurrentValue(TextProperty, Editor.Text);
            }
        };

        this.GetObservable(TextProperty).Subscribe(text =>
        {
            if (Editor.Text != text)
            {
                Editor.Text = text ?? string.Empty;
            }
        });

        if (Design.IsDesignMode)
        {
            Text = "{\n  \"key\": \"value\",\n  \"number\": 123,\n  \"boolean\": true,\n  \"nullValue\": null\n}";
        }
    }

    private static IHighlightingDefinition BuildHighlighting(bool dark)
    {
        var keyColor = dark ? "#9CDCFE" : "#0451A5";
        var strColor = dark ? "#CE9178" : "#A31515";
        var numColor = dark ? "#B5CEA8" : "#098658";
        var kwColor = dark ? "#569CD6" : "#0000FF";
        var xshd = $"""
            <?xml version="1.0"?>
            <SyntaxDefinition name="JSON" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Key" foreground="{keyColor}" />
              <Color name="String" foreground="{strColor}" />
              <Color name="Number" foreground="{numColor}" />
              <Color name="Keyword" foreground="{kwColor}" fontWeight="bold" />
              <RuleSet>
                <Rule color="Key">"([^"\\]|\\.)*"(?=\s*:)</Rule>
                <Rule color="String">"([^"\\]|\\.)*"</Rule>
                <Rule color="Number">-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?</Rule>
                <Keywords color="Keyword">
                  <Word>true</Word>
                  <Word>false</Word>
                  <Word>null</Word>
                </Keywords>
              </RuleSet>
            </SyntaxDefinition>
            """;
        using var reader = XmlReader.Create(new StringReader(xshd));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    private void FormatJson_Click(object? sender, RoutedEventArgs e)
    {
        if (IsReadOnly) return;
        try
        {
            using var document = JsonDocument.Parse(Editor.Text, SDocumentOptions);
            Editor.Text = JsonSerializer.Serialize(document.RootElement, SIndentedOptions);
        }
        catch (JsonException)
        {
            txtValidation.Text = ResUI.OperationFailed;
        }
    }

    private void Copy_Click(object? sender, RoutedEventArgs e) => Editor.Copy();

    private void Paste_Click(object? sender, RoutedEventArgs e) { if (!IsReadOnly) Editor.Paste(); }

    private void SelectAll_Click(object? sender, RoutedEventArgs e) => Editor.SelectAll();
}
