using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using Drawing = System.Drawing;
using System.Drawing.Drawing2D;

namespace NumaControl
{
    internal static class Themes
    {
        internal static readonly string[] Ids = { "standard", "win11-light", "win11-dark", "ami-bios" };
        internal static string[] Names { get { return new[] { L10n.T("Стандартная"), L10n.T("Стиль Windows 11 Светлая"), L10n.T("Стиль Windows 11 Темная"), L10n.T("Стиль AMI BIOS") }; } }
        internal static string Current = "standard";
        internal static string Indicator { get { return Current == "ami-bios" ? "■" : "●"; } }
        private static readonly FontFamily StandardFont = new FontFamily("Segoe UI"), BiosFont = new FontFamily("Consolas");
        internal static FontFamily Font { get { return Current == "ami-bios" ? BiosFont : StandardFont; } }
        internal static double TextSize(double size) { return Current == "ami-bios" && size < 14 ? size + 1 : size; }
        private static readonly Dictionary<string, Brush> brushes = new Dictionary<string, Brush>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, byte[]> icons = new Dictionary<string, byte[]>();
        private static readonly Dictionary<string, string> roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string[]> colors = new Dictionary<string, string[]> {
            {"Bg", new[]{"#101218", "#F3F3F3", "#202020", "#CCCCCC"}},
            {"Sidebar", new[]{"#151820", "#E9EDF2", "#191919", "#BABABA"}},
            {"Panel", new[]{"#191C25", "#FFFFFF", "#2B2B2B", "#DEDEDE"}},
            {"Text", new[]{"#EEF0F8", "#1B1B1B", "#F2F2F2", "#171717"}},
            {"Muted", new[]{"#949DB1", "#556171", "#B6BAC3", "#3A3A3A"}},
            {"Line", new[]{"#343A4B", "#C7CDD6", "#494949", "#000080"}},
            {"Button", new[]{"#252A37", "#F8FAFD", "#353535", "#D2D2D2"}},
            {"Input", new[]{"#11141C", "#FFFFFF", "#202020", "#EEEEEE"}},
            {"SwitchOffTrack", new[]{"#3C4253", "#C7CDD6", "#494949", "#6E6E6E"}},
            {"SwitchOffThumb", new[]{"#D4D8E4", "#1B1B1B", "#F2F2F2", "#FFFFFF"}},
            {"Accent", new[]{"#B2A9FF", "#005FB8", "#76B9ED", "#0000AA"}},
            {"OnAccent", new[]{"#161225", "#FFFFFF", "#101820", "#FFFFFF"}},
            {"Selected", new[]{"#38314F", "#DCEBFA", "#243D50", "#0000AA"}},
            {"LibrarySelected", new[]{"#262638", "#DCEBFA", "#243D50", "#F2F2F2"}},
            {"NavigationSelected", new[]{"#292638", "#DCEBFA", "#243D50", "#F2F2F2"}},
            {"SelectedText", new[]{"#E4DDFF", "#004A90", "#C3E5FF", "#FFFFFF"}},
            {"Hover", new[]{"#242732", "#E6EBF2", "#404040", "#D2D2E0"}},
            {"Success", new[]{"#45D59D", "#087942", "#65DCA9", "#005520"}},
            {"Warning", new[]{"#F5C75C", "#946000", "#F5CC72", "#754500"}},
            {"Error", new[]{"#F28D9B", "#BD2439", "#FF9C9C", "#A0001B"}},
            {"SuccessBg", new[]{"#142B26", "#E3F4E9", "#183A30", "#CDE1CD"}},
            {"WarningBg", new[]{"#342C1B", "#FFF0CA", "#42351C", "#ECDCAD"}}
        };
        static Themes()
        {
            Add("Bg", "#101218"); Add("Sidebar", "#151820"); Add("Panel", "#191C25");
            Add("Text", "#EEF0F8 #E4E7F2 #DDE1EF #DCE1ED #E8EBF5 #B5BED1 #BBC2D3");
            Add("Muted", "#949DB1 #7D879C #A7A0C7 #7F899D #747F95 #606B80 #8993A9 #7F889D #8FA4A1 #84968E #9AA5BB");
            Add("Line", "#2A2F3C #282D39 #2A2F3B #343A4B #353B4E #343A49 #41495D #48435F #655336");
            Add("SwitchOffTrack", "#3C4253"); Add("SwitchOffThumb", "#D4D8E4");
            Add("Button", "#252A37 #292D3C #32303D"); Add("Input", "#11141C #13161E");
            Add("Accent", "#ACA3FF #B2A9FF #A49AF5 #A59BFF #C4BCFF #9B8BEE #B6AFFF #AFA6FA #8C83FF");
            Add("OnAccent", "#161225 #171324 #211C35"); Add("Selected", "#38314F #655DA4"); Add("LibrarySelected", "#262638"); Add("NavigationSelected", "#292638");
            Add("SelectedText", "#DCD3FF"); Add("Hover", "#242732");
            Add("Success", "#8ED9B7 #9DD8BF #45D59D #72CCB0"); Add("Warning", "#E5B578 #E9CFA0 #F5C75C #FFB66C");
            Add("Error", "#F28D9B #F07E87"); Add("SuccessBg", "#1D2429 #111B1B #142B26"); Add("WarningBg", "#30291D #342C1B");
        }
        private static void Add(string role, string values) { foreach (string value in values.Split(' ')) roles[value] = role; }
        internal static bool Valid(string id) { return Array.IndexOf(Ids, id) >= 0; }
        internal static void Set(string id) { if (!Valid(id)) throw new ArgumentException(L10n.T("Неизвестная тема.")); Current = id; brushes.Clear(); }
        internal static string Color(string role) { return colors[role][Array.IndexOf(Ids, Current)]; }
        internal static string Map(string color)
        {
            string role;
            // Preserve the original palette for the standard theme, except requested status colors.
            if (Current == "standard" && color != "#8ED9B7" && color != "#9DD8BF") return color;
            return roles.TryGetValue(color, out role) ? Color(role) : color;
        }
        internal static Brush Brush(string color)
        {
            string mapped = Map(color); Brush brush;
            if (!brushes.TryGetValue(mapped, out brush)) { var solid = (SolidColorBrush)new BrushConverter().ConvertFromString(mapped); solid.Freeze(); brushes[mapped] = brush = solid; }
            return brush;
        }
        internal static Brush BadgeBrush(string color)
        {
            Color value = (Color)ColorConverter.ConvertFromString(Map(color));
            Color background = (Color)ColorConverter.ConvertFromString(Map("#32303D")); double first = Luminance(value), second = Luminance(background);
            if ((Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05) < 4.5) return Brush(Color("Accent"));
            return Brush(color);
        }
        private static double Channel(byte value) { double s = value / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        private static double Luminance(Color c) { return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B); }
        internal static string Transform(string xaml)
        {
            xaml = Regex.Replace(xaml, "#[0-9A-Fa-f]{6}(?![0-9A-Fa-f])", m => Map(m.Value));
            if (Current == "ami-bios")
            {
                xaml = xaml.Replace("Segoe UI", "Consolas").Replace("<Ellipse", "<Rectangle").Replace("</Ellipse>", "</Rectangle>");
                xaml = Regex.Replace(xaml, "FontSize=\"([0-9]+)\"", m => "FontSize=\"" + TextSize(double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"");
                xaml = Regex.Replace(xaml, "Property=\"FontSize\" Value=\"([0-9]+)\"", m => "Property=\"FontSize\" Value=\"" + TextSize(double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"");
                xaml = Regex.Replace(xaml, "(<Style(?=[\\s>])[^>]*>)", "$1<Setter Property=\"TextElement.FontFamily\" Value=\"Consolas\"/>");
                xaml = Regex.Replace(xaml, "<(TextBlock|ui:OpticalText|Button|TextBox|CheckBox|ComboBox|ComboBoxItem|ToggleButton)(?=[\\s/>])([^>]*?)(/?)>", m => m.Groups[2].Value.Contains("FontFamily=") ? m.Value : "<" + m.Groups[1].Value + " FontFamily=\"Consolas\"" + m.Groups[2].Value + m.Groups[3].Value + ">");
                xaml = Regex.Replace(xaml, "CornerRadius=\"[0-9]+\"", "CornerRadius=\"0\""); xaml = Regex.Replace(xaml, "Property=\"CornerRadius\" Value=\"[0-9]+\"", "Property=\"CornerRadius\" Value=\"0\"");
                xaml = xaml.Replace("M9,2 L23,2 Q30,2 30,9 L30,23 Q30,30 23,30 L9,30 Q2,30 2,23 L2,9 Q2,2 9,2 Z", "M2,2 L30,2 L30,30 L2,30 Z");
                xaml = xaml.Replace("M10.5,8 A2.5,2.5 0 1 1 5.5,8 A2.5,2.5 0 1 1 10.5,8", "M6,6 L10,6 L10,10 L6,10 Z");
                xaml = xaml.Replace("RadiusX=\"1\" RadiusY=\"1\"", "RadiusX=\"0\" RadiusY=\"0\"");
            }
            return xaml;
        }
        internal static byte[] IconBytes()
        {
            byte[] cached; if (icons.TryGetValue(Current, out cached)) return cached;
            int[] sizes = {16, 20, 24, 32, 40, 48, 64, 128, 256}; var images = new List<byte[]>();
            foreach (int size in sizes)
            using (var bitmap = new Drawing.Bitmap(size, size))
            using (Drawing.Graphics g = Drawing.Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.ScaleTransform(size / 256f, size / 256f);
                using (var shape = new GraphicsPath())
                {
                    if (Current == "ami-bios") shape.AddRectangle(new Drawing.Rectangle(12,12,232,232));
                    else { shape.AddArc(12,12,72,72,180,90); shape.AddArc(172,12,72,72,270,90); shape.AddArc(172,172,72,72,0,90); shape.AddArc(12,172,72,72,90,90); shape.CloseFigure(); }
                    using (var fill = new Drawing.SolidBrush(Drawing.ColorTranslator.FromHtml(Color("Accent")))) g.FillPath(fill,shape);
                }
                using (var path = new GraphicsPath())
                {
                    path.AddPolygon(new[]{new Drawing.Point(67,194),new Drawing.Point(67,62),new Drawing.Point(103,62),new Drawing.Point(155,144),new Drawing.Point(155,62),new Drawing.Point(190,62),new Drawing.Point(190,194),new Drawing.Point(156,194),new Drawing.Point(102,110),new Drawing.Point(102,194)});
                    using (var fill = new Drawing.SolidBrush(Drawing.ColorTranslator.FromHtml(Color("OnAccent")))) g.FillPath(fill,path);
                }
                using (var png = new MemoryStream()) { bitmap.Save(png,Drawing.Imaging.ImageFormat.Png); images.Add(png.ToArray()); }
            }
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            {
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length); int offset = 6 + 16 * sizes.Length;
                for (int i=0;i<sizes.Length;i++) { writer.Write((byte)(sizes[i]==256?0:sizes[i])); writer.Write((byte)(sizes[i]==256?0:sizes[i])); writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(images[i].Length); writer.Write(offset); offset += images[i].Length; }
                foreach (byte[] png in images) writer.Write(png); byte[] result = stream.ToArray(); icons[Current] = result; return result;
            }
        }
        internal static string SaveIcon(string directory)
        {
            string folder = Path.Combine(directory,"icons","controller"); Directory.CreateDirectory(folder); string path = Path.Combine(folder,Current+".ico");
            byte[] desired = IconBytes(); bool matches = false;
            if (File.Exists(path))
            {
                byte[] existing = File.ReadAllBytes(path); matches = existing.Length == desired.Length;
                for (int i = 0; matches && i < desired.Length; i++) matches = existing[i] == desired[i];
            }
            if (!matches) File.WriteAllBytes(path,desired); return path;
        }
    }
}
