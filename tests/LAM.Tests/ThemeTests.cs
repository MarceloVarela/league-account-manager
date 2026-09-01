using System.Xml.Linq;
using Xunit;

namespace LAM.Tests;

/// <summary>
/// Guards the dark theme against the mistake that produced an unreadable Region dropdown: a style
/// that sets colours but no <c>Template</c>.
///
/// WPF's default control templates draw their own light chrome and popups, and they inherit whatever
/// <c>Foreground</c> the style supplies. So a property-only style on a control that has chrome does
/// not produce a dark control — it produces near-white text on WPF's white popup, which is exactly
/// what happened to <c>ComboBox</c>, <c>CheckBox</c>, <c>TabItem</c> and <c>DatePicker</c>.
///
/// These parse the XAML as plain XML rather than loading it into WPF, so they need no STA thread, no
/// rendering, and no PresentationFramework reference — they are structural checks on the file.
/// </summary>
public sealed class ThemeTests
{
    /// <summary>
    /// Controls that draw chrome of their own — borders, popups, item containers, glyphs. Colouring
    /// one of these without replacing its template is the bug under test.
    /// </summary>
    private static readonly HashSet<string> ChromeControls = new(StringComparer.Ordinal)
    {
        "Button", "RepeatButton", "ToggleButton",
        "TextBox", "PasswordBox", "RichTextBox",
        "ComboBox", "ComboBoxItem",
        "CheckBox", "RadioButton",
        "TabControl", "TabItem",
        "ListBox", "ListBoxItem", "ListView", "ListViewItem",
        "TreeView", "TreeViewItem",
        "Menu", "MenuItem", "ContextMenu", "Separator",
        "ToolTip", "ScrollBar", "ScrollViewer", "Slider", "ProgressBar",
        "DatePicker", "Calendar", "Expander", "GroupBox", "ToolBar",
    };

    /// <summary>
    /// Controls with no chrome of their own: they render only their content, so setting a colour on
    /// them is complete and correct. Listed explicitly so the rule stays honest rather than being
    /// waved away case by case.
    /// </summary>
    private static readonly HashSet<string> NoChromeControls = new(StringComparer.Ordinal)
    {
        "TextBlock", "Label", "Border", "Window", "Page", "UserControl",
        "Thumb", "Panel", "StackPanel", "Grid", "ItemsControl",
    };

    // ---- locating the source tree ------------------------------------------

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LeagueAccountManager.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not find LeagueAccountManager.sln above " + AppContext.BaseDirectory);
    }

    private static string ThemePath() => Path.Combine(RepositoryRoot(), "src", "LAM.App", "Theme.xaml");

    private static string ViewsDirectory() => Path.Combine(RepositoryRoot(), "src", "LAM.App", "Views");

    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    // ---- reading the theme --------------------------------------------------

    private sealed record ThemeStyle(string TargetType, IReadOnlyCollection<string> SetProperties)
    {
        public bool Sets(string property) => SetProperties.Contains(property);
        public bool SetsColour => Sets("Foreground") || Sets("Background");
    }

    private static List<ThemeStyle> ReadStyles()
    {
        var document = XDocument.Load(ThemePath());
        var styles = new List<ThemeStyle>();

        foreach (var style in document.Descendants(Presentation + "Style"))
        {
            var targetType = Normalise((string?)style.Attribute("TargetType"));
            if (targetType is null) continue;

            // Only the setters belonging to *this* style — a Style nested inside a ControlTemplate
            // (the scrollbar thumb, say) is its own style and is visited separately.
            var properties = style
                .Elements(Presentation + "Setter")
                .Select(setter => (string?)setter.Attribute("Property"))
                .Where(name => name is not null)
                .Select(name => name!)
                .ToHashSet(StringComparer.Ordinal);

            styles.Add(new ThemeStyle(targetType, properties));
        }

        return styles;
    }

    /// <summary>Turns <c>{x:Type ComboBox}</c> or <c>local:Thing</c> into a bare type name.</summary>
    private static string? Normalise(string? targetType)
    {
        if (string.IsNullOrWhiteSpace(targetType)) return null;

        var value = targetType.Trim();
        if (value.StartsWith('{'))
        {
            var parts = value.Trim('{', '}').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            value = parts.Length > 1 ? parts[^1] : value;
        }

        var colon = value.LastIndexOf(':');
        if (colon >= 0) value = value[(colon + 1)..];

        return value.Trim();
    }

    // ---- the rules ----------------------------------------------------------

    /// <summary>
    /// Target types that receive a template from somewhere in the dictionary.
    ///
    /// Grouped by type rather than checked per style, because a derived style
    /// (<c>PrimaryButton</c> is <c>BasedOn</c> the base Button style) inherits the template without
    /// restating it. What matters is that the control type gets a template at all.
    /// </summary>
    private static HashSet<string> TemplatedTypes()
        => ReadStyles()
            .Where(style => style.Sets("Template"))
            .Select(style => style.TargetType)
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void A_style_that_sets_colours_on_a_chrome_control_must_also_set_a_template()
    {
        var templated = TemplatedTypes();

        var offenders = ReadStyles()
            .Where(style => ChromeControls.Contains(style.TargetType))
            .Where(style => style.SetsColour && !templated.Contains(style.TargetType))
            .Select(style => style.TargetType)
            .Distinct()
            .ToList();

        Assert.True(offenders.Count == 0,
            "These styles set colours without replacing the control template, so WPF will keep its " +
            "default light chrome and draw the theme's near-white text onto it — the exact bug that " +
            "made the Region dropdown unreadable: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Every_chrome_control_used_in_a_view_is_themed()
    {
        var themed = TemplatedTypes();
        var missing = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.GetFiles(ViewsDirectory(), "*.xaml"))
        {
            var document = XDocument.Load(file);

            foreach (var element in document.Descendants())
            {
                if (element.Name.Namespace != Presentation) continue;

                var name = element.Name.LocalName;
                if (!ChromeControls.Contains(name)) continue;
                if (themed.Contains(name)) continue;

                // ScrollViewer is composed of the ScrollBars we do theme; it draws no chrome itself.
                if (name == "ScrollViewer") continue;

                missing.Add(name + "  (" + Path.GetFileName(file) + ")");
            }
        }

        Assert.True(missing.Count == 0,
            "These controls are used in a view but have no templated style in Theme.xaml, so they " +
            "will render with WPF's light defaults: " + string.Join(", ", missing));
    }

    [Fact]
    public void The_controls_that_caused_the_bug_are_all_templated_now()
    {
        // Named explicitly so a future refactor cannot quietly drop one of them.
        string[] required =
        [
            "ComboBox", "ComboBoxItem", "CheckBox", "TabItem", "TabControl",
            "ContextMenu", "MenuItem", "Separator", "ToolTip", "ScrollBar", "ProgressBar",
        ];

        var missing = required.Where(control => !TemplatedTypes().Contains(control)).ToList();

        Assert.True(missing.Count == 0, "Not templated: " + string.Join(", ", missing));
    }

    [Fact]
    public void ComboBox_keeps_the_parts_wpf_requires()
    {
        // A ComboBox template silently misbehaves without these two named parts: the popup never
        // opens, and an editable combo cannot be typed into.
        var theme = File.ReadAllText(ThemePath());

        Assert.Contains("PART_Popup", theme, StringComparison.Ordinal);
        Assert.Contains("PART_EditableTextBox", theme, StringComparison.Ordinal);

        // The editable text box needs its own bare template that still exposes a content host.
        Assert.Contains("ComboBoxEditableTextBox", theme, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_entry_templates_keep_their_content_host()
    {
        // TextBox and PasswordBox render nothing at all without PART_ContentHost.
        var theme = File.ReadAllText(ThemePath());
        var hosts = theme.Split("PART_ContentHost").Length - 1;

        Assert.True(hosts >= 3,
            "Expected a PART_ContentHost in the TextBox, PasswordBox and editable-ComboBox templates; found " + hosts);
    }

    [Fact]
    public void No_control_style_is_left_colour_only()
    {
        // The broader form of the first rule: catches a style that sets a colour on a chrome control
        // even if a future edit adds the control to neither list.
        var unclassified = ReadStyles()
            .Select(style => style.TargetType)
            .Distinct()
            .Where(type => !ChromeControls.Contains(type) && !NoChromeControls.Contains(type))
            .ToList();

        Assert.True(unclassified.Count == 0,
            "Theme.xaml styles a control this test does not classify. Add it to ChromeControls (it " +
            "draws its own chrome and needs a Template) or NoChromeControls (it renders only its " +
            "content): " + string.Join(", ", unclassified));
    }
}
