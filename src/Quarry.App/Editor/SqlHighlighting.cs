using System.Security;
using System.Text;
using System.Xml;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace Quarry.App.Editor;

/// <summary>Builds the T-SQL highlighting definition from <see cref="SqlKeywords"/>, with light and dark palettes.</summary>
public static class SqlHighlighting
{
    private static IHighlightingDefinition? _light;
    private static IHighlightingDefinition? _dark;

    public static IHighlightingDefinition Get(bool dark)
        => dark ? _dark ??= Build(DarkPalette) : _light ??= Build(LightPalette);

    private static readonly Dictionary<string, string> LightPalette = new()
    {
        ["Comment"] = "#008000",
        ["String"] = "#A31515",
        ["Keyword"] = "#0000FF",
        ["Function"] = "#B000B0",
        ["DataType"] = "#1F7F9F",
        ["Variable"] = "#6F42C1",
        ["Number"] = "#098658",
        ["Operator"] = "#707070",
    };

    private static readonly Dictionary<string, string> DarkPalette = new()
    {
        ["Comment"] = "#6A9955",
        ["String"] = "#CE9178",
        ["Keyword"] = "#569CD6",
        ["Function"] = "#DCDCAA",
        ["DataType"] = "#4EC9B0",
        ["Variable"] = "#9CDCFE",
        ["Number"] = "#B5CEA8",
        ["Operator"] = "#A0A0A0",
    };

    private static IHighlightingDefinition Build(Dictionary<string, string> palette)
    {
        var xml = new StringBuilder();
        xml.AppendLine("""<SyntaxDefinition name="TSQL" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">""");
        foreach (var (name, color) in palette)
            xml.AppendLine($"""  <Color name="{name}" foreground="{color}" />""");
        xml.AppendLine("""
              <RuleSet ignoreCase="true">
                <Span color="Comment" begin="--" />
                <Span color="Comment" multiline="true" begin="/\*" end="\*/" />
                <Span color="String" multiline="true" begin="N?'" end="'" />
                <Span begin="\[" end="\]" />
                <Span begin="&quot;" end="&quot;" />
                <Rule color="Variable">@@?[\w#$@]+</Rule>
            """);
        AppendKeywords(xml, "Keyword", SqlKeywords.Keywords);
        AppendKeywords(xml, "DataType", SqlKeywords.DataTypes);
        AppendKeywords(xml, "Function", SqlKeywords.Functions);
        xml.AppendLine("""
                <Rule color="Number">\b0[xX][0-9a-fA-F]*|\b\d+(\.\d+)?([eE][+-]?\d+)?\b</Rule>
                <Rule color="Operator">[=&lt;&gt;!+\-*/%&amp;|^~]</Rule>
              </RuleSet>
            </SyntaxDefinition>
            """);

        using var reader = XmlReader.Create(new StringReader(xml.ToString()));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    private static void AppendKeywords(StringBuilder xml, string color, IEnumerable<string> words)
    {
        xml.AppendLine($"""    <Keywords color="{color}">""");
        foreach (var word in words.Distinct(StringComparer.OrdinalIgnoreCase))
            xml.AppendLine($"      <Word>{SecurityElement.Escape(word)}</Word>");
        xml.AppendLine("    </Keywords>");
    }
}
