using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace NumaControl
{
    internal static partial class Tests
    {
        private const double VisualTolerance = 2.0;
        private static int visualChecks;
        private static readonly List<string> visualCases = new List<string>();

        private static void VisualCheck(bool condition, string context, string criterion)
        {
            Check(condition, "VISUAL / " + context + " / " + criterion, false);
            visualChecks++;
        }

        private static Rect ElementBounds(FrameworkElement element, Visual surface)
        {
            return element.TransformToAncestor(surface).TransformBounds(new Rect(element.RenderSize));
        }

        private static string VisualName(FrameworkElement element)
        {
            return string.IsNullOrEmpty(element.Name) ? element.GetType().Name : element.Name;
        }

        private static void CheckFullyVisible(FrameworkElement element, FrameworkElement surface, string context)
        {
            Rect bounds = ElementBounds(element, surface);
            Rect available = new Rect(surface.RenderSize);
            available.Inflate(VisualTolerance, VisualTolerance);
            VisualCheck(element.IsVisible && bounds.Width > 0 && bounds.Height > 0 && available.Contains(bounds), context,
                VisualName(element) + " fits inside " + VisualName(surface));
        }

        private static void CheckDisjoint(FrameworkElement surface, string context, params FrameworkElement[] items)
        {
            for (int i = 0; i < items.Length; i++)
            {
                Rect first = ElementBounds(items[i], surface);
                for (int j = i + 1; j < items.Length; j++)
                {
                    Rect overlap = Rect.Intersect(first, ElementBounds(items[j], surface));
                    VisualCheck(overlap.IsEmpty || overlap.Width <= VisualTolerance || overlap.Height <= VisualTolerance, context,
                        VisualName(items[i]) + " and " + VisualName(items[j]) + " do not overlap");
                }
            }
        }

        private static void CheckAlignedRow(FrameworkElement surface, string context, string name, params FrameworkElement[] items)
        {
            Rect[] bounds = items.Select(item => ElementBounds(item, surface)).ToArray();
            VisualCheck(bounds.Max(rect => rect.Top) - bounds.Min(rect => rect.Top) <= VisualTolerance &&
                bounds.Max(rect => rect.Bottom) - bounds.Min(rect => rect.Bottom) <= VisualTolerance, context,
                name + " share top and bottom edges");
            CheckDisjoint(surface, context, items);
        }

        private static Color? OpaqueBackground(DependencyObject element)
        {
            for (DependencyObject current = element; current != null; current = VisualTreeHelper.GetParent(current))
            {
                Brush brush = null;
                if (current is Border) brush = ((Border)current).Background;
                else if (current is Panel) brush = ((Panel)current).Background;
                else if (current is TextBlock) brush = ((TextBlock)current).Background;
                else if (current is Control) brush = ((Control)current).Background;
                var color = brush as SolidColorBrush;
                if (color != null && color.Color.A == 255 && color.Opacity == 1) return color.Color;
            }
            return null;
        }

        private static void CheckVisualText(TextBlock text, string context, string name)
        {
            VisualCheck(text != null && text.IsVisible && !string.IsNullOrWhiteSpace(text.Text) && text.ActualWidth > 0 && text.ActualHeight > 0,
                context, name + " has a rendered caption");
            // Measure without trimming so intentionally or accidentally inserted ellipses cannot hide clipping.
            var measurement = new TextBlock
            {
                Text = text.Text, FontFamily = text.FontFamily, FontSize = text.FontSize, FontWeight = text.FontWeight,
                FontStretch = text.FontStretch, FontStyle = text.FontStyle, TextWrapping = text.TextWrapping,
                FlowDirection = text.FlowDirection, Language = text.Language, Padding = text.Padding,
                LineStackingStrategy = text.LineStackingStrategy, LineHeight = text.LineHeight
            };
            TextOptions.SetTextFormattingMode(measurement, TextOptions.GetTextFormattingMode(text));
            measurement.Measure(new Size(text.TextWrapping == TextWrapping.NoWrap ? double.PositiveInfinity : text.ActualWidth, double.PositiveInfinity));
            VisualCheck(measurement.DesiredSize.Width <= text.ActualWidth + VisualTolerance &&
                measurement.DesiredSize.Height <= text.ActualHeight + VisualTolerance, context, name + " fits without clipping");
            var foreground = text.Foreground as SolidColorBrush;
            Color? background = OpaqueBackground(text);
            double ratio = foreground != null && background.HasValue ? Contrast(foreground.Color.ToString(), background.Value.ToString()) : 0;
            VisualCheck(ratio >= 4.5, context, name + " meets 4.5:1 text contrast (measured " + ratio.ToString("0.00", CultureInfo.InvariantCulture) + ")");
        }

        private static TextBlock RenderedCaption(FrameworkElement item)
        {
            return Descendants(item).OfType<TextBlock>().FirstOrDefault(text => text.IsVisible && !string.IsNullOrWhiteSpace(text.Text));
        }

        private static void CheckVisualButtons(Window window, FrameworkElement surface, string context, params string[] names)
        {
            Button[] buttons = names.Select(name => Control<Button>(window, name)).ToArray();
            foreach (Button button in buttons)
            {
                CheckFullyVisible(button, surface, context);
                CheckVisualText(RenderedCaption(button), context, button.Name);
            }
            CheckDisjoint(surface, context, buttons);
        }

        private static void CheckWindowChrome(Window window, string context)
        {
            var surface = (FrameworkElement)window.Content;
            CheckVisualButtons(window, surface, context, "AddGame", "LibraryNav", "SettingsNav", "LogNav", "DiagnosticsNav");
            var nav = new[] { "LibraryNav", "SettingsNav", "LogNav", "DiagnosticsNav" }.Select(name => ElementBounds(Control<Button>(window, name), surface)).ToArray();
            VisualCheck(nav.Max(rect => rect.Left) - nav.Min(rect => rect.Left) <= VisualTolerance &&
                nav.Max(rect => rect.Width) - nav.Min(rect => rect.Width) <= VisualTolerance &&
                nav.Max(rect => rect.Height) - nav.Min(rect => rect.Height) <= VisualTolerance, context, "sidebar navigation uses one grid");
            CheckVisualText(Control<TextBlock>(window, "EngineLabel"), context, "monitoring badge");
            CheckVisualText(Control<TextBlock>(window, "VersionLabel"), context, "version footer");
        }

        private static void CheckPageHeading(Window window, string context)
        {
            var surface = (FrameworkElement)window.Content;
            Button pause = Control<Button>(window, "PauseAll");
            Rect action = ElementBounds(pause, surface);
            CheckVisualButtons(window, surface, context, "PauseAll");
            foreach (string name in new[] { "PageTitle", "PageSubtitle" })
            {
                TextBlock text = Control<TextBlock>(window, name);
                CheckVisualText(text, context, name);
                DrawingGroup drawing = VisualTreeHelper.GetDrawing(text);
                Rect ink = drawing == null ? Rect.Empty : text.TransformToAncestor(surface).TransformBounds(drawing.Bounds);
                Rect overlap = Rect.Intersect(ink, action);
                VisualCheck(!ink.IsEmpty && (overlap.IsEmpty || overlap.Width <= VisualTolerance || overlap.Height <= VisualTolerance),
                    context, name + " paints clear of the Pause action");
            }
        }

        private static void CheckLibraryVisuals(Window window, string context)
        {
            var viewer = Control<ScrollViewer>(window, "LibraryView");
            var summary = Control<Grid>(window, "SummaryCards");
            var cards = summary.Children.OfType<Border>().ToArray();
            CheckAlignedRow(summary, context, "library summary cards", cards);
            VisualCheck(cards.Length == 3 && cards.Max(card => card.ActualWidth) - cards.Min(card => card.ActualWidth) <= VisualTolerance,
                context, "library summary cards have equal widths");
            foreach (string name in new[] { "ObserverMetric", "CpuMetric", "AppCoreMetric", "LiveState", "CpuMapTitle", "MaskLabel" })
                CheckVisualText(Control<TextBlock>(window, name), context, name);
            TextBlock title = Control<TextBlock>(window, "CpuMapTitle"), mask = Control<TextBlock>(window, "MaskLabel");
            Border info = Control<Border>(window, "CpuModeInfo");
            CheckDisjoint(viewer, context, title, mask, info);
            VisualCheck(Math.Abs(title.TranslatePoint(new Point(0, title.BaselineOffset), viewer).Y -
                mask.TranslatePoint(new Point(0, mask.BaselineOffset), viewer).Y) <= 1, context, "map title and mask share a baseline");
            CheckVisualButtons(window, (FrameworkElement)viewer.Content, context, "LaunchGame", "DesktopShortcut", "EditGame");
        }

        private static void CheckCpuTileStates(Window window, string context)
        {
            var tile = Descendants(Control<WrapPanel>(window, "CpuMap")).OfType<ToggleButton>().First();
            bool? previous = tile.IsChecked;
            try
            {
                foreach (bool selected in new[] { true, false })
                {
                    tile.SetCurrentValue(ToggleButton.IsCheckedProperty, selected);
                    window.UpdateLayout();
                    TextBlock label = RenderedCaption(tile);
                    string state = selected ? "selected CPU tile" : "unselected CPU tile";
                    CheckVisualText(label, context, state);
                    CheckFullyVisible(label, tile, context);
                    Rect text = ElementBounds(label, tile);
                    VisualCheck(Math.Abs(text.Left + text.Width / 2 - tile.ActualWidth / 2) <= VisualTolerance &&
                        Math.Abs(text.Top + text.Height / 2 - tile.ActualHeight / 2) <= VisualTolerance, context, state + " label is centered");
                }
            }
            finally { tile.SetCurrentValue(ToggleButton.IsCheckedProperty, previous); }
        }

        private static ScrollContentPresenter Viewport(ScrollViewer viewer)
        {
            return Descendants(viewer).OfType<ScrollContentPresenter>().First();
        }

        private static void CheckSecondaryPage(Window window, string nav, string context)
        {
            CheckPageHeading(window, context);
            if (nav == "SettingsNav")
            {
                foreach (string name in new[] { "LanguageSelector", "ThemeSelector" })
                {
                    ComboBox selector = Control<ComboBox>(window, name);
                    var selection = Descendants(selector).OfType<TextBlock>().Single(text => text.Name == "ThemeSelectionText");
                    CheckVisualText(selection, context, name + " selected value");
                    CheckFullyVisible(selection, selector, context);
                }
                CheckVisualText(Control<TextBlock>(window, "AboutVersion"), context, "About version");
                CheckVisualButtons(window, Control<StackPanel>(window, "SettingsBody"), context,
                    "Interval1000", "Interval2000", "Interval5000", "ExportProfiles", "ImportProfiles");
            }
            else if (nav == "DiagnosticsNav")
            {
                ScrollViewer viewer = Control<ScrollViewer>(window, "DiagnosticsView");
                foreach (string section in new[] { "Status", "Memory", "Cpu", "Handles", "Affinity", "Heartbeat" })
                {
                    string key = "Diagnostic" + section;
                    TextBlock caption = Control<TextBlock>(window, key + "Caption");
                    TextBlock value = Control<TextBlock>(window, key);
                    Border marker = Control<Border>(window, key + "Status");
                    CheckVisualText(caption, context, key + " title");
                    CheckVisualText(value, context, key + " value");
                    CheckDisjoint(viewer, context, caption, marker, value);
                }
                var body = Control<StackPanel>(window, "DiagnosticsBody");
                CheckVisualButtons(window, body, context, "RefreshDiagnostics", "SaveDiagnosticReport", "OpenReports");
                var pairGrid = body.Children.OfType<Grid>().Single();
                var cards = pairGrid.Children.OfType<Border>().ToArray();
                CheckAlignedRow(pairGrid, context, "upper diagnostics cards", cards[0], cards[1]);
                CheckAlignedRow(pairGrid, context, "lower diagnostics cards", cards[2], cards[3]);
            }
            else CheckVisualButtons(window, Control<Grid>(window, "LogView"), context, "RefreshLog");
        }

        private static void CheckProfileEditorVisuals(Window editor, string context)
        {
            var surface = (FrameworkElement)editor.Content;
            CheckVisualButtons(editor, surface, context, "DeleteProfile", "CancelProfile", "SaveProfile");
            var save = Control<Button>(editor, "SaveProfile");
            var cancel = Control<Button>(editor, "CancelProfile");
            CheckAlignedRow(surface, context, "editor footer actions", cancel, save);
            var viewer = Descendants(editor).OfType<ScrollViewer>().First();
            string[] names = { "GameNameInput", "ProcessNameInput", "GamePathInput", "LaunchTargetInput", "ArgumentsInput" };
            Rect[] fields = names.Select(name => ElementBounds(Control<TextBox>(editor, name), (FrameworkElement)viewer.Content)).ToArray();
            VisualCheck(fields.Max(rect => rect.Left) - fields.Min(rect => rect.Left) <= VisualTolerance &&
                fields.Max(rect => rect.Right) - fields.Min(rect => rect.Right) <= VisualTolerance, context, "all profile fields share column edges");
            foreach (string name in names)
            {
                var field = Control<TextBox>(editor, name);
                string label = System.Windows.Automation.AutomationProperties.GetName(field);
                var caption = Descendants(viewer).OfType<TextBlock>().Single(text => text.Text == label);
                CheckVisualText(caption, context, name + " label");
                CheckDisjoint((FrameworkElement)viewer.Content, context, caption, field);
            }
        }
    }
}
