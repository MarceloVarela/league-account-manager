using System.Text.RegularExpressions;
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

        // Path draws a vector and nothing else — it is how the registration marks are rendered.
        "Path",

        // ContentControl is used for the framed-panel decorator, whose style supplies its own
        // Template outright rather than colouring a stock one.
        "ContentControl",

        // The bare Control style exists only to carry a FocusVisualStyle, so there is no chrome of
        // its own to replace.
        "Control",
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

    private static string SourceDirectory() => Path.Combine(RepositoryRoot(), "src", "LAM.App");

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
    public void The_two_palettes_define_exactly_the_same_tokens()
    {
        // A key present in one palette and missing from the other is a crash the moment that theme
        // is selected and something looks it up — and it would only show on the theme nobody tested.
        var light = TokensIn("Light.xaml");
        var dark = TokensIn("Dark.xaml");

        Assert.True(light.SetEquals(dark),
            "Light and Dark must define the same token keys. Only in light: "
            + string.Join(", ", light.Except(dark).Order()) + ". Only in dark: "
            + string.Join(", ", dark.Except(light).Order()) + ".");
    }

    [Fact]
    public void Colours_live_in_the_palettes_and_nowhere_else()
    {
        // Theme.xaml holds structure. A brush defined there would be the same in both themes and
        // would silently survive the swap — which is exactly how a dark theme ends up with one
        // stubbornly light panel.
        var theme = File.ReadAllText(ThemePath());

        Assert.DoesNotContain("<SolidColorBrush x:Key=", theme, StringComparison.Ordinal);
    }

    [Fact]
    public void Palette_tokens_are_looked_up_dynamically()
    {
        // StaticResource binds once at load, so a runtime theme swap would not reach it.
        var theme = File.ReadAllText(ThemePath());

        foreach (var token in new[] { "Ink", "Ground", "Surface", "Divider", "MutedText", "Accent" })
        {
            Assert.DoesNotContain("{StaticResource " + token + "}", theme, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Metadata_never_drops_below_the_eleven_pixel_floor()
    {
        // Measured, not preference: the earlier draft used 10px mono at 55% ink and came out under
        // 4:1. Both the size and the opacity are floors.
        var theme = File.ReadAllText(ThemePath());
        var mono = theme[theme.IndexOf("x:Key=\"Mono\"", StringComparison.Ordinal)..];
        mono = mono[..mono.IndexOf("</Style>", StringComparison.Ordinal)];

        Assert.Contains("FontSize\" Value=\"11\"", mono, StringComparison.Ordinal);
    }

    private static HashSet<string> TokensIn(string file)
    {
        var path = Path.Combine(RepositoryRoot(), "src", "LAM.App", "Themes", file);
        var text = File.ReadAllText(path);

        return
        [
            .. System.Text.RegularExpressions.Regex
                .Matches(text, "<SolidColorBrush x:Key=\"([A-Za-z0-9]+)\"")
                .Select(m => m.Groups[1].Value),

            // Raw <Color> entries count too. They were unguarded, which is the half of each palette
            // that gradient stops, shadow effects and animation targets read from.
            .. System.Text.RegularExpressions.Regex
                .Matches(text, "<Color x:Key=\"([A-Za-z0-9]+)\"")
                .Select(m => m.Groups[1].Value),
        ];
    }

    [Fact]
    public void Views_look_palette_tokens_up_dynamically_too()
    {
        // The failure this catches, seen for real: Theme.xaml was converted to DynamicResource but
        // the views were not, so switching to dark moved the styled controls and left every band
        // that sets its own Background behind — light chrome wearing dark text, i.e. invisible.
        string[] tokens =
        [
            "Ground", "Surface", "Ink", "MutedText", "Divider", "Accent", "AccentPressed",
            "Background", "SurfaceRaised", "BorderBrushSoft", "Warning", "Danger", "Success",
        ];

        var offenders = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.GetFiles(ViewsDirectory(), "*.xaml"))
        {
            var text = File.ReadAllText(file);

            foreach (var token in tokens)
            {
                if (text.Contains("{StaticResource " + token + "}", StringComparison.Ordinal))
                    offenders.Add(Path.GetFileName(file) + " -> " + token);
            }
        }

        Assert.True(offenders.Count == 0,
            "These views bind a palette token with StaticResource, so a theme swap will not reach " +
            "them and the window ends up half-light, half-dark: " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_deleted_hatch_is_not_painted_on_any_surface()
    {
        // The design removed the diagonal hatch outright — "it reads cheap" — and made the rank
        // plate, icon frame, header bands and lock ground solid raised surfaces. A stray reference
        // would put the old texture back on one panel and nowhere else, which reads as a bug.
        var offenders = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.GetFiles(ViewsDirectory(), "*.xaml").Append(ThemePath()))
        {
            var text = File.ReadAllText(file);

            // Match the MECHANISM, not two long-deleted resource names. The previous form grepped
            // for "BlueprintHatch" and "LockHatch", neither of which has existed in the tree for as
            // long as this test has — so it passed vacuously while a hatch survived elsewhere.
            foreach (Match use in Regex.Matches(text, @"(?:DrawingBrush|VisualBrush)[^>]*x:Key=""(\w+)"""))
            {
                var name = use.Groups[1].Value;

                // The warning plate's edge bar is the one pattern the design keeps.
                if (name.Contains("Hazard", StringComparison.OrdinalIgnoreCase)) continue;

                offenders.Add(Path.GetFileName(file) + ": " + name);
            }

            foreach (Match use in Regex.Matches(text, @"Resource (\w*Hatch\w*)\}"))
            {
                if (use.Groups[1].Value.Contains("Hazard", StringComparison.OrdinalIgnoreCase)) continue;

                offenders.Add(Path.GetFileName(file) + ": " + use.Groups[1].Value);
            }
        }

        Assert.True(offenders.Count == 0,
            "The design deleted these patterns; only the warning plate's edge bar keeps one: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void Registration_marks_are_not_on_the_account_cards()
    {
        // The handoff is explicit: marks go on the window shell, large panels and primary buttons,
        // and NOT on account cards. Four per card times nine cards is 36 crosshairs on one screen,
        // which is what turns a spec sheet into noise.
        var main = File.ReadAllText(Path.Combine(ViewsDirectory(), "MainWindow.xaml"));

        var cardStart = main.IndexOf("DataType=\"{x:Type vm:AccountTile}\"", StringComparison.Ordinal);
        Assert.True(cardStart > 0, "Could not find the account-card DataTemplate.");

        // IndexOf("</DataTemplate>") lands on the NESTED tag-chip template's close tag, which left
        // roughly the last third of the card unexamined. Take the whole resources block instead.
        var cardEnd = main.IndexOf("</ItemsControl.Resources>", cardStart, StringComparison.Ordinal);
        Assert.True(cardEnd > cardStart, "Could not find the end of the card template.");

        var card = main[cardStart..cardEnd];

        Assert.DoesNotContain("RegistrationMark", card, StringComparison.Ordinal);
    }

    [Fact]
    public void The_focus_ring_is_applied_to_every_interactive_style()
    {
        // It existed as a keyed style with no call sites for the whole of the previous build, so what
        // really drew was Windows' dotted marquee — the one thing the design forbids by name.
        //
        // Counting occurrences of the string was not enough: it stayed green while six keyed shell
        // styles (the ones the shell is actually built from) had none. Name them.
        var text = File.ReadAllText(ThemePath())
                   + File.ReadAllText(Path.Combine(ViewsDirectory(), "ShellChrome.xaml"));

        string[] mustHaveRing =
        [
            "PrimaryButton", "ChromeButton", "ScrollPageButton", "NavTab", "Segment",
        ];

        var missing = new List<string>();

        foreach (var key in mustHaveRing)
        {
            var at = text.IndexOf("x:Key=\"" + key + "\"", StringComparison.Ordinal);
            if (at < 0) continue;

            var end = text.IndexOf("</Style>", at, StringComparison.Ordinal);
            if (end < 0) end = text.Length;

            if (!text[at..end].Contains("FocusVisualStyle", StringComparison.Ordinal)) missing.Add(key);
        }

        Assert.True(missing.Count == 0,
            "These interactive styles draw the OS dotted marquee because they set no FocusVisualStyle: "
            + string.Join(", ", missing));
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

    [Fact]
    public void Every_card_binding_is_notified_by_Refresh()
    {
        // Tiles are reused across filters, so a property the card binds but Refresh() never raises
        // can no longer repaint in place. This was masked for the whole of the previous build by the
        // grid being discarded and rebuilt on every keystroke.
        var card = CardTemplate();

        var bound = new SortedSet<string>(StringComparer.Ordinal);

        foreach (Match m in Regex.Matches(card, @"\{Binding\s+(?:Path=)?([A-Za-z_]\w*)"))
        {
            bound.Add(m.Groups[1].Value);
        }

        var tile = File.ReadAllText(Path.Combine(SourceDirectory(), "ViewModels", "AccountTile.cs"));

        var declared = Regex.Matches(tile, @"public\s+[\w<>?\[\], ]+?\s+(\w+)\s*(?:=>|\{)")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var notified = Regex.Matches(tile, @"OnPropertyChanged\(nameof\((\w+)\)\)")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var silent = bound.Where(declared.Contains).Where(b => !notified.Contains(b)).ToList();

        Assert.True(silent.Count == 0,
            "The card binds these AccountTile properties but Refresh() never raises them, so they "
            + "can never repaint in place: " + string.Join(", ", silent));
    }

    [Fact]
    public void The_accounts_grid_selects_its_cell_templates_by_type()
    {
        // WPF copies an explicit ItemsControl.ItemTemplate onto every container's ContentTemplate,
        // and ContentPresenter only falls through to an implicit DataType lookup when ContentTemplate
        // is null. So an explicit ItemTemplate silently defeats type selection: the add tile gets
        // drawn with the ACCOUNT-CARD template, every binding fails, and because a failed binding
        // yields UnsetValue each Visibility converter reverts to Visible — a blank card wearing a
        // blinking LIVE badge and a warning plate.
        var main = File.ReadAllText(Path.Combine(ViewsDirectory(), "MainWindow.xaml"));

        var grid = main.IndexOf("ItemsSource=\"{Binding GridItems}\"", StringComparison.Ordinal);
        Assert.True(grid > 0, "Could not find the accounts grid.");

        var end = main.IndexOf("</ItemsControl>", grid, StringComparison.Ordinal);
        var block = main[grid..end];

        Assert.False(block.Contains("<ItemsControl.ItemTemplate>", StringComparison.Ordinal),
            "The accounts grid sets an explicit ItemTemplate, which defeats the DataType lookup and "
            + "paints the add tile with the account-card template. Use implicit templates in "
            + "ItemsControl.Resources instead.");

        Assert.Contains("DataType=\"{x:Type vm:AddAccountTile}\"", block, StringComparison.Ordinal);
        Assert.Contains("DataType=\"{x:Type vm:AccountTile}\"", block, StringComparison.Ordinal);
    }

    [Fact]
    public void No_palette_token_is_resolved_in_C_sharp()
    {
        // The C# equivalent of a StaticResource: Resources["Ink"] captures whichever palette was
        // live at the time and never updates. It is invisible to the XAML-only guard above, and it
        // cost a settings pane whose own theme toggle left every label in the outgoing palette.
        var tokens = TokensIn("Light.xaml");
        var offenders = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.GetFiles(SourceDirectory(), "*.cs", SearchOption.AllDirectories))
        {
            // The converter resolves per call BY DESIGN — that is how it follows a theme swap.
            if (Path.GetFileName(file) == "TierBrushConverter.cs") continue;

            var text = File.ReadAllText(file);

            foreach (Match m in Regex.Matches(text, @"(?:Resources\[|FindResource\(|TryFindResource\()""(\w+)"""))
            {
                if (tokens.Contains(m.Groups[1].Value))
                {
                    offenders.Add(Path.GetFileName(file) + ": " + m.Groups[1].Value);
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "These resolve a palette token in C#, which freezes it against the palette that was live "
            + "at the time: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Both_palettes_carry_every_rank_tier()
    {
        // Rank is the one place colour carries meaning, and a tier missing from one palette falls
        // back to muted grey in that theme only — which reads as a rendering bug, not a design.
        string[] tiers =
        [
            "Iron", "Bronze", "Silver", "Gold", "Platinum", "Emerald", "Diamond",
            "Master", "Grandmaster", "Challenger", "Immortal", "Ascendant", "Radiant", "Unranked",
        ];

        foreach (var file in new[] { "Light.xaml", "Dark.xaml" })
        {
            var keys = TokensIn(file);

            var missing = tiers.Select(t => "Tier" + t).Where(k => !keys.Contains(k)).ToList();

            Assert.True(missing.Count == 0, file + " is missing rank tiers: " + string.Join(", ", missing));
        }
    }

    [Fact]
    public void No_view_raises_a_stock_message_box()
    {
        // A frameless radius-0 app that raises an OS message box puts two visual languages on screen
        // at once, and the OS one ignores the theme entirely — a dark window produced a light dialog.
        // Dialog.Say / Dialog.Confirm replace all 36 former call sites.
        var offenders = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.GetFiles(SourceDirectory(), "*.cs", SearchOption.AllDirectories))
        {
            // Dialog itself falls back to one when there is no window to own or centre on.
            if (Path.GetFileName(file) == "Dialog.xaml.cs") continue;

            if (File.ReadAllText(file).Contains("MessageBox.Show", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.True(offenders.Count == 0,
            "These raise a stock Windows message box; use Dialog.Say or Dialog.Confirm: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void No_element_sets_a_property_its_own_style_also_triggers()
    {
        // WPF precedence: a value written as an ATTRIBUTE on an element is a local value, and local
        // values outrank style triggers. So an element that sets Background="..." and also carries a
        // Style whose trigger sets Background will never change colour — the trigger fires, the
        // binding resolves, and the result is silently discarded.
        //
        // This cost two failed fixes of the LP bar (it rendered as an empty strip both times) and
        // would have shipped the form chart dead on arrival. It is invisible in a build and invisible
        // in a test that only reads view-models, which is exactly why it needs a guard.
        var offenders = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.GetFiles(SourceDirectory(), "*.xaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);

            // Each <Foo.Style> block, paired with the element tag that opens immediately before it.
            foreach (Match block in Regex.Matches(
                         text, @"<(\w+)([^>]*?)>\s*<\1\.Style>(.*?)</\1\.Style>", RegexOptions.Singleline))
            {
                var attributes = block.Groups[2].Value;
                var style = block.Groups[3].Value;

                // Only trigger setters matter: a plain setter and an attribute can coexist, since the
                // attribute is simply the winner and that is usually deliberate.
                var triggered = Regex.Matches(style, @"<Style\.Triggers>(.*?)</Style\.Triggers>",
                        RegexOptions.Singleline)
                    .SelectMany(t => Regex.Matches(t.Groups[1].Value, @"<Setter\s+Property=""(\w+)""")
                        .Select(m => m.Groups[1].Value))
                    .ToHashSet(StringComparer.Ordinal);

                foreach (var property in triggered)
                {
                    if (Regex.IsMatch(attributes, @"\b" + property + @"\s*="))
                    {
                        offenders.Add(Path.GetFileName(file) + ": <" + block.Groups[1].Value
                                      + " " + property + "=...> is overridden by its own trigger");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "These elements set a property as an attribute AND trigger it from their own Style. The "
            + "attribute is a local value and always wins, so the trigger is dead. Move the default "
            + "into the Style as a plain Setter: " + string.Join("; ", offenders));
    }

    [Fact]
    public void Every_window_adopts_the_shell_chrome()
    {
        // ShellWindow is what sets Foreground={Ink} and FontFamily={BodyFont}. A Window without it
        // inherits WPF's defaults, so every TextBlock that does not set its own Foreground renders
        // BLACK — which is what left "VAULT LOCKED" invisible on the dark lock panel, in the one
        // window nobody thought to check because it is the main one.
        var offenders = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.GetFiles(ViewsDirectory(), "*.xaml"))
        {
            var text = File.ReadAllText(file);

            if (!text.TrimStart().StartsWith("<Window", StringComparison.Ordinal)) continue;

            var head = text[..text.IndexOf('>')];

            if (!head.Contains("ShellWindow", StringComparison.Ordinal)
                && !head.Contains("Foreground=", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.True(offenders.Count == 0,
            "These windows carry neither ShellWindow nor an explicit Foreground, so their unstyled "
            + "text renders in the system default (black on the dark theme): "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void No_AccountTile_template_binds_a_property_that_does_not_exist()
    {
        // A dead binding is silent: WPF logs it at debug and renders nothing at all. Renaming
        // RankBrush to TierKey left two bindings in the detail window pointing at a property that no
        // longer existed, so the rank rendered uncoloured and the avatar frame lost its border, with
        // no error anywhere and nothing failing.
        //
        // Scoped to regions whose DataContext is provably an AccountTile: templates declared
        // DataType="{x:Type vm:AccountTile}", plus the detail window's header, which sits outside any
        // ItemsControl in a window that assigns DataContext = the tile. Anything inside a nested
        // ItemsControl binds a row type this test cannot see, so it is deliberately not inspected.
        var tile = File.ReadAllText(Path.Combine(SourceDirectory(), "ViewModels", "AccountTile.cs"));

        var declared = Regex.Matches(tile, @"public\s+[\w<>?\[\], ]+?\s+(\w+)\s*(?:=>|\{)")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var offenders = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.GetFiles(ViewsDirectory(), "*.xaml"))
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            foreach (var region in TileRegions(text, name))
            {
                foreach (Match m in Regex.Matches(region, @"\{Binding\s+(?:Path=)?([A-Z]\w*)[\s,}]"))
                {
                    var bound = m.Groups[1].Value;

                    // RelativeSource bindings resolve against the element, not the tile.
                    if (bound is "IsMouseOver" or "IsChecked" or "Tag" or "DataContext") continue;

                    if (!declared.Contains(bound)) offenders.Add(name + ": " + bound);
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "These bind a property that does not exist on AccountTile, so they render nothing and "
            + "log only at debug level: " + string.Join(", ", offenders));
    }

    /// <summary>The stretches of a view whose DataContext is provably an AccountTile.</summary>
    private static IEnumerable<string> TileRegions(string text, string file)
    {
        foreach (Match m in Regex.Matches(
                     text, @"DataType=""\{x:Type vm:AccountTile\}""(.*?)</DataTemplate>",
                     RegexOptions.Singleline))
        {
            yield return m.Groups[1].Value;
        }

        // AccountDetailWindow sets DataContext = the tile, so its header — everything before the
        // first TabControl — binds the tile directly.
        if (file == "AccountDetailWindow.xaml")
        {
            var tabs = text.IndexOf("<TabControl", StringComparison.Ordinal);
            if (tabs > 0) yield return text[..tabs];
        }
    }

    private static string CardTemplate()
    {
        var main = File.ReadAllText(Path.Combine(ViewsDirectory(), "MainWindow.xaml"));

        var start = main.IndexOf("DataType=\"{x:Type vm:AccountTile}\"", StringComparison.Ordinal);
        var end = main.IndexOf("</ItemsControl.Resources>", start, StringComparison.Ordinal);

        return main[start..end];
    }
}
