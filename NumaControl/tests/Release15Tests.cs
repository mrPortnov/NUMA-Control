using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NumaControl
{
    internal static partial class Tests
    {
        private static void Release15Functional(Topology cpu, string fixture)
        {
            Check(Configuration.Defaults(cpu).Games.Count == 0 && new ProfileStore(Path.Combine(root,"fresh-defaults")).Load(cpu).Games.Count == 0, "1.5 fresh profile stores have no preloaded games");
            Check(new GameProfile().HighPriority, "1.5 newly added games request high priority by default");
            string folder = Path.Combine(root,"priority-migration"); var store = new ProfileStore(folder); Configuration old = SampleConfiguration(cpu); store.Save(old);
            string json = File.ReadAllText(store.FilePath); json = System.Text.RegularExpressions.Regex.Replace(json, ",?\"HighPriority\":false", ""); json = json.Replace("\"Theme\":\"standard\",", ""); File.WriteAllText(store.FilePath,json);
            Configuration migrated = store.Load(cpu); Check(migrated.Games.All(g=>!g.HighPriority) && migrated.Theme == "standard", "1.5 old profile JSON preserves unmanaged priority and default theme");
            migrated.Games[0].HighPriority = true; migrated.Theme = "win11-light"; store.Save(migrated);
            Check(store.Load(cpu).Games[0].HighPriority && store.Load(cpu).Theme == "win11-light", "1.5 priority and theme survive JSON round trip");
            migrated.Theme = "unknown"; Reject(()=>migrated.Validate(cpu,true), "1.5 unknown imported theme is rejected");
            foreach (double ratio in new[]{0.0,0.6999,0.7,0.8999,0.9,1.0,1.5}) Check(HealthPresentation.Level(ratio) == (ratio>=0.9?2:ratio>=0.7?1:0), "1.5 exact status thresholds at "+ratio);
            Configuration c = new Configuration(); var game = new GameProfile { Name="Priority fixture", ProcessName=Path.GetFileNameWithoutExtension(fixture), ExePath=fixture, Mask=cpu.AllMask.ToString("X"), Enabled=true }; c.Games.Add(game);
            for (int run=0;run<2;run++)
            using (Process process = Process.Start(new ProcessStartInfo(fixture) {UseShellExecute=false,CreateNoWindow=true}))
            using (var engine = new WatchEngine(c,cpu,new AppLog(Path.Combine(root,"priority-log"))))
            {
                try
                {
                    process.PriorityClass = ProcessPriorityClass.Normal;
                    Check(engine.Scan(c)[game.Id].Applied == 1, "1.5 priority fixture is matched on launch "+run);
                    using(var h=Native.Open(process.Id,false)) Check(Native.ReadPriority(h)==0x80 && Native.ReadMask(h)==game.CpuMask, "1.5 high priority and affinity are actually set in Windows "+run);
                    process.PriorityClass = ProcessPriorityClass.Normal; engine.Scan(c); process.Refresh(); Check(process.PriorityClass==ProcessPriorityClass.High, "1.5 external priority reset is restored "+run);
                    Configuration disabled = c.Copy(); disabled.Games[0].HighPriority=false; process.PriorityClass=ProcessPriorityClass.Normal; engine.Scan(disabled); process.Refresh(); Check(process.PriorityClass==ProcessPriorityClass.Normal,"1.5 disabled priority policy leaves native priority unchanged "+run);
                    Configuration paused=c.Copy(); paused.Paused=true; engine.Scan(paused); process.Refresh(); Check(process.PriorityClass==ProcessPriorityClass.Normal,"1.5 global pause prevents raising priority "+run);
                    Configuration off=c.Copy(); off.Games[0].Enabled=false; engine.Scan(off); process.Refresh(); Check(process.PriorityClass==ProcessPriorityClass.Normal,"1.5 disabled observer prevents raising priority "+run);
                    Configuration wrong=c.Copy(); wrong.Games[0].ExePath=Path.Combine(Path.GetDirectoryName(fixture),"other",Path.GetFileName(fixture)); engine.Scan(wrong); process.Refresh(); Check(process.PriorityClass==ProcessPriorityClass.Normal,"1.5 wrong executable path cannot change priority "+run);
                    using(var h=Native.Open(process.Id,false)) Reject(()=>Native.ApplyHighPriority(h),"1.5 query-only process handle cannot change priority "+run);
                }
                finally { if(!process.HasExited) {process.Kill(); process.WaitForExit();} }
            }
        }
        private static void Release15UiChecks(Application app)
        {
            string folder=Path.Combine(root,"release-1.5-ui"); var dialogs=new ScriptedInteraction(); var environment=new AppEnvironment(folder){Interaction=dialogs,ExitOnFatal=false};
            using(var controller=new AppController(app,false,folder,environment))
            {
                controller.Start(); controller.ShowWindow(); Window w=MainWindow(app);
                Check(controller.Config.Games.Count==0 && Control<Border>(w,"EmptyState").Visibility==Visibility.Visible && Control<ListBox>(w,"GameList").Items.Count==0,"1.5 real fresh controller starts with an empty library");
                Check(Control<TextBlock>(w,"Toast").Text=="", "1.5 neutral footer contains no close-window notice"); Capture(w,"release-1.5-empty");
                dialogs.Editors.Enqueue(editor=>{Check(Control<CheckBox>(editor,"HighPriorityToggle").IsChecked==true,"1.5 add-game editor defaults to high priority"); Control<TextBox>(editor,"GameNameInput").Text="Тест приоритета"; Control<TextBox>(editor,"ProcessNameInput").Text="NumaMissingPriority"; Press(editor,"SaveProfile");});
                Press(w,"AddGame"); string id=controller.Config.Games.Single().Id; Check(controller.Config.Games[0].HighPriority,"1.5 profile editor persists priority switch");
                dialogs.Editors.Enqueue(editor=>{Control<CheckBox>(editor,"HighPriorityToggle").IsChecked=false; Press(editor,"SaveProfile");}); Press(w,"EditGame"); Check(!controller.Config.Games.Single().HighPriority,"1.5 profile editor can disable priority management");
                dialogs.Editors.Enqueue(editor=>{Control<CheckBox>(editor,"HighPriorityToggle").IsChecked=true; Press(editor,"SaveProfile");}); Press(w,"EditGame"); Check(controller.Config.Games.Single().HighPriority,"1.5 profile editor can re-enable high priority");
                byte[] previousIcon=null;
                for(int themeIndex=0;themeIndex<Themes.Ids.Length;themeIndex++)
                {
                    Press(w,"SettingsNav"); ComboBox choice=Control<ComboBox>(w,"ThemeSelector"); Check(choice.Items.Count==4,"1.5 settings exposes all four themes"); choice.SelectedIndex=themeIndex; Pump(); w=MainWindow(app);
                    Check(controller.Config.Theme==Themes.Ids[themeIndex] && controller.Config.Games.Single().Id==id,"1.5 theme change preserves selected profile and configuration "+Themes.Current);
                    foreach(int width in new[]{960,1220,1440})
                    {
                        w.Width=width; Press(w,"LibraryNav"); Pump();
                        double[] centers=new[]{"ObserverMetric","CpuMetric","AppCoreMetric"}.Select(n=>PaintedCenter(w,Control<TextBlock>(w,n)).Y).ToArray();
                        double[] hints=new[]{"ObserverHint","CpuHint","AppCoreHint"}.Select(n=>PaintedCenter(w,Control<TextBlock>(w,n)).Y).ToArray();
                        Check(centers.Max()-centers.Min()<=1 && hints.Max()-hints.Min()<=1,"1.5 summary cards align actual painted glyphs "+Themes.Current+" "+width);
                        Check(Math.Abs(TextBaseline(w,Control<TextBlock>(w,"CpuMapTitle"))-TextBaseline(w,Control<TextBlock>(w,"MaskLabel")))<=0.01,"1.5 CPU map title and HEX share one exact baseline "+Themes.Current+" "+width);
                        Check(Control<TextBlock>(w,"CpuMapTitle").FontSize+1==Control<TextBlock>(w,"MaskLabel").FontSize,"1.5 CPU map HEX is one size larger than the title "+Themes.Current+" "+width);
                        Check(Control<TextBlock>(w,"SelectedGameCaption").FontSize==Control<TextBlock>(w,"CpuMapTitle").FontSize && Control<TextBlock>(w,"SelectedGameCaption").FontWeight==Control<TextBlock>(w,"CpuMapTitle").FontWeight,"1.5 selected-game caption and CPU map title share size and weight "+Themes.Current+" "+width);
                        TextBlock maskLabel=Control<TextBlock>(w,"MaskLabel"), mapTitle=Control<TextBlock>(w,"CpuMapTitle"); string maskText=maskLabel.Text;
                        foreach(string mask in new[]{"0x1","0x3FFC","0xFFFFFFFFFFFFFFFF"})
                        {
                            maskLabel.Text=mask; Pump(); PaintedCenter(w,maskLabel); Rect ink=VisualTreeHelper.GetDrawing(maskLabel).Bounds;
                            double visibleHeight=Math.Abs(maskLabel.TranslatePoint(ink.BottomRight,w).Y-maskLabel.TranslatePoint(ink.TopLeft,w).Y);
                            Console.WriteLine("Header glyphs: "+Themes.Current+" titleHeight="+VisualTreeHelper.GetDrawing(mapTitle).Bounds.Height+" maskHeight="+visibleHeight+" titleFaces="+String.Join(",",GlyphFonts(VisualTreeHelper.GetDrawing(mapTitle)))+" maskFaces="+String.Join(",",GlyphFonts(VisualTreeHelper.GetDrawing(maskLabel))));
                            Check(maskLabel.FontFamily.Source=="Consolas" && GlyphFonts(VisualTreeHelper.GetDrawing(maskLabel)).All(f=>f.IndexOf("consol",StringComparison.OrdinalIgnoreCase)>=0),"1.5 HEX uses actual Consolas glyphs one size above heading "+Themes.Current+" "+width+" "+mask);
                            Check(Math.Abs(TextBaseline(w,maskLabel)-TextBaseline(w,mapTitle))<=0.01 && maskLabel.LayoutTransform.Value.IsIdentity && maskLabel.RenderTransform.Value.IsIdentity,"1.5 HEX shares heading baseline without scaling or translation "+Themes.Current+" "+width+" "+mask);
                            foreach(double scale in new[]{1.0,1.5}) {
                                Rect titlePixels=RasterBounds(w,mapTitle,scale),maskPixels=RasterBounds(w,maskLabel,scale);
                                Check(Math.Abs(titlePixels.Top-maskPixels.Top)<=2 && Math.Abs(TextBaseline(w,mapTitle)-TextBaseline(w,maskLabel))<=0.01,"1.5 heading and Consolas HEX raster capital tops align within two DIP and exact baseline "+Themes.Current+" "+width+" "+mask+" "+scale);
                            }
                        }
                        maskLabel.Text=maskText; Pump();
                        foreach(double scale in new[]{1.0,1.5})
                        {
                            double[] pixels=new[]{"ObserverMetric","CpuMetric","AppCoreMetric"}.Select(n=>RasterCenter(w,Control<TextBlock>(w,n),scale).Y).ToArray();
                            Console.WriteLine("Raster library: "+Themes.Current+" width="+width+" scale="+scale+" cards="+String.Join(",",pixels.Select(v=>v.ToString("F2")))+" title="+RasterCenter(w,Control<TextBlock>(w,"CpuMapTitle"),scale).Y+" HEX="+RasterCenter(w,Control<TextBlock>(w,"MaskLabel"),scale).Y);
                            Check(pixels.Max()-pixels.Min()<=1.5 && Math.Abs(TextBaseline(w,mapTitle)-TextBaseline(w,maskLabel))<=0.01,"1.5 raster glyph alignment at "+scale+"x "+Themes.Current+" "+width);
                        }
                    }
                    Press(w,"SettingsNav");
                    Check(((SolidColorBrush)w.Background).Color==(Color)ColorConverter.ConvertFromString(Themes.Color("Bg")),"1.5 actual window surface matches theme "+Themes.Current);
                    var tray=(System.Windows.Forms.NotifyIcon)typeof(AppController).GetField("tray",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(controller);
                    Check(tray.ContextMenuStrip.BackColor==System.Drawing.ColorTranslator.FromHtml(Themes.Color("Panel")),"1.5 tray menu follows theme "+Themes.Current);
                    var themedMenu=tray.ContextMenuStrip;themedMenu.PerformLayout();themedMenu.Size=themedMenu.GetPreferredSize(System.Drawing.Size.Empty);
                    Check(themedMenu.Font.Name==(Themes.Current=="ami-bios"?"Consolas":"Segoe UI"),"1.5 tray menu uses intended theme font "+Themes.Current);
                    Check(themedMenu.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().All(item=>item.Image!=null && item.Image.Width>=64 && item.Image.Height>=64),"1.5 every themed tray command retains a high-resolution icon "+Themes.Current);
                    using(var bitmap=new System.Drawing.Bitmap(themedMenu.Width,themedMenu.Height)){themedMenu.DrawToBitmap(bitmap,new System.Drawing.Rectangle(0,0,bitmap.Width,bitmap.Height));bitmap.Save(Path.Combine(root,"visuals","release-1.5-"+Themes.Current+"-tray.png"),System.Drawing.Imaging.ImageFormat.Png);}
                    ComboBox selector=Control<ComboBox>(w,"ThemeSelector"); selector.IsDropDownOpen=true; Pump();
                    Check(selector.ItemContainerGenerator.ContainerFromIndex(3)!=null,"1.5 themed dropdown opens all four choices "+Themes.Current); selector.IsDropDownOpen=false; Pump();
                    Check(((SolidColorBrush)Control<TextBlock>(w,"ThemeSelectionText").Foreground).Color==((SolidColorBrush)selector.Foreground).Color,"1.5 selected theme text has explicit readable foreground "+Themes.Current);
                    using (var editor = new EditorLifetime(new ProfileEditor(w,null,controller.Topology,controller.Config,dialogs)))
                    { editor.Window.Show(); Pump(); TextBox input=Control<TextBox>(editor.Window,"GameNameInput"); Check(((SolidColorBrush)input.CaretBrush).Color==((SolidColorBrush)input.Foreground).Color,"1.5 text caret remains visible in current theme "+Themes.Current); }
                    byte[] icon=Themes.IconBytes(); Check(icon.Length>500 && (previousIcon==null || !previousIcon.SequenceEqual(icon)),"1.5 themed application ICO changes with palette "+Themes.Current); previousIcon=icon;
                    string cachedIcon=Themes.SaveIcon(folder);File.WriteAllBytes(cachedIcon,new byte[]{0,1,2,3});Themes.SaveIcon(folder);
                    Check(File.ReadAllBytes(cachedIcon).SequenceEqual(icon),"1.5 cached theme icon is refreshed after palette update "+Themes.Current);
                    foreach(string page in new[]{"LibraryNav","SettingsNav","DiagnosticsNav"})
                    {
                        Press(w,page); w.Width=960; w.Height=760; Pump(); Capture(w,"release-1.5-"+Themes.Current+"-"+page);
                        Check(!Descendants(w).OfType<TextBlock>().Any(t=>t.Text.Contains("; ")),"1.5 visible prose uses complete sentences "+Themes.Current+" "+page);
                        if(Themes.Current=="ami-bios") Check(Descendants(w).OfType<TextBlock>().Where(t=>t.IsVisible).All(t=>t.FontFamily.Source=="Consolas"),"1.5 BIOS font is uniform on page "+page);
                        if(Themes.Current=="ami-bios") {
                            string[] fonts=GlyphFonts(VisualTreeHelper.GetDrawing(Control<TextBlock>(w,"PageTitle"))).ToArray();
                            Console.WriteLine("BIOS heading glyph faces: "+String.Join(",",fonts));
                            Check(fonts.Length>0 && fonts.All(f=>f.IndexOf("consol",StringComparison.OrdinalIgnoreCase)>=0),"1.5 BIOS heading is actually rendered with Consolas "+page);
                        }
                    }
                    Press(w,"PauseAll"); Check(((SolidColorBrush)Control<TextBlock>(w,"EngineLabel").Foreground).Color==(Color)ColorConverter.ConvertFromString(Themes.Color("Warning")),"1.5 paused sidebar has warning color "+Themes.Current); Press(w,"PauseAll");
                    Press(w,"DiagnosticsNav"); FieldInfo current=typeof(SelfDiagnostics).GetField("current",BindingFlags.Instance|BindingFlags.NonPublic); ResourceSample actual=controller.Health.Current;
                    TextBlock status=Control<TextBlock>(w,"DiagnosticStatus"); Border statusDot=Control<Border>(w,"DiagnosticStatusStatus");
                    foreach(string key in new[]{"Status","Memory","Cpu","Handles","Affinity","Heartbeat"})
                    foreach(double scale in new[]{1.0,1.5}) Check(Math.Abs(RasterCenter(w,Control<TextBlock>(w,"Diagnostic"+key+"Caption"),scale).Y-RasterCenter(w,Control<Border>(w,"Diagnostic"+key+"Status"),scale).Y)<=1.5,"1.5 every diagnostic marker follows its title raster line "+Themes.Current+" "+key+" "+scale);
                    double sy=PaintedCenter(w,status).Y, cy=PaintedCenter(w,Control<TextBlock>(w,"DiagnosticStatusCaption")).Y, dy=PaintedCenter(w,statusDot).Y;
                    Console.WriteLine("Optical self control: "+Themes.Current+" value="+sy+" caption="+cy+" marker="+dy);
                    Check(Math.Abs(sy-cy)<=1 && Math.Abs(sy-dy)<=1,"1.5 self-control caption, message and indicator share one optical line "+Themes.Current);
                    foreach(double scale in new[]{1.0,1.5}) Check(Math.Abs(RasterCenter(w,status,scale).Y-RasterCenter(w,Control<TextBlock>(w,"DiagnosticStatusCaption"),scale).Y)<=1.5,"1.5 self-control raster alignment at "+scale+"x "+Themes.Current);
                    var optical=new OpticalText { Text="1 / 1",FontSize=18,Width=220,Height=32,VerticalAlignment=VerticalAlignment.Center };
                    Window probe=new Window { Title="Проверка глифов",Width=260,Height=120,Content=optical,ShowInTaskbar=false }; probe.Show(); Pump(); optical.Visibility=Visibility.Collapsed; optical.Text="Автовыбор"; optical.Visibility=Visibility.Visible; Pump();
                    DrawingGroup actualDrawing=VisualTreeHelper.GetDrawing(optical); Check(actualDrawing!=null && Math.Abs(PaintedCenter(probe,optical).Y-optical.TranslatePoint(new Point(0,optical.ActualHeight/2),probe).Y+((TranslateTransform)optical.RenderTransform).Y)<=1,"1.5 hidden text is realigned when shown again "+Themes.Current); probe.Close();
                    if(Themes.Current=="ami-bios")
                    {
                        Check(Contrast(Themes.Color("Accent"),Themes.Color("Panel"))>=7,"1.5 BIOS saturated blue clearly contrasts with neutral gray panels");
                        foreach(string role in new[]{"Text","Muted","Success","Warning","Error"}) Check(Contrast(Themes.Color(role),Themes.Color("Panel"))>=4.5,"1.5 BIOS readable text contrast "+role);
                        Press(w,"LibraryNav"); CheckBox switchProbe=Control<CheckBox>(w,"EnabledToggle"); bool? previous=switchProbe.IsChecked;
                        foreach(bool enabled in new[]{false,true}) {
                            switchProbe.IsChecked=enabled; switchProbe.ApplyTemplate(); Pump();
                            var track=(Border)switchProbe.Template.FindName("Track",switchProbe);var knob=(System.Windows.Shapes.Shape)switchProbe.Template.FindName("Knob",switchProbe);
                            string trackColor=((SolidColorBrush)track.Background).Color.ToString(),knobColor=((SolidColorBrush)knob.Fill).Color.ToString();
                            Check(Contrast(trackColor,knobColor)>=4.5,"1.5 BIOS switch thumb is clearly visible in "+(enabled?"on":"off")+" state");
                        }
                        switchProbe.IsChecked=previous; Press(w,"DiagnosticsNav");
                        Check(!Descendants(w).OfType<System.Windows.Shapes.Ellipse>().Any() && Control<Border>(w,"DiagnosticStatusStatus").CornerRadius.TopLeft==0,"1.5 BIOS indicators and switch knobs use square geometry");
                        Check(Descendants(w).OfType<TextBlock>().Where(t=>t.IsVisible).All(t=>t.FontFamily.Source=="Consolas"),"1.5 BIOS uses one explicit font for all visible text and headings");
                        Check(Control<TextBlock>(w,"DiagnosticMemory").FontSize>=14,"1.5 BIOS small diagnostic text is enlarged for readability");
                        Press(w,"LibraryNav"); Check(!Descendants(w).OfType<Border>().Any(b=>b.CornerRadius.TopLeft>0 || b.CornerRadius.TopRight>0),"1.5 BIOS application borders have no rounded corners"); Press(w,"DiagnosticsNav");
                    }
                    try
                    {
                        foreach(string key in new[]{"Memory","Cpu","Handles","Heartbeat"})
                        foreach(double fraction in new[]{0.69,0.7,0.89,0.9})
                        {
                            ResourceSample sample=Healthy(60); if(key=="Memory") sample.PrivateMiB=512*fraction; else if(key=="Cpu") sample.CpuOneCorePercent=90*fraction; else if(key=="Handles") sample.Handles=(int)(6000*fraction); else sample.UiHeartbeatAgeSeconds=45*fraction;
                            current.SetValue(controller.Health,sample); typeof(AppController).GetMethod("UpdateDiagnostics",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(controller,null);
                            Color expected=(Color)ColorConverter.ConvertFromString(Themes.Color(HealthPresentation.Role(HealthPresentation.Level(fraction))));
                            Check(((SolidColorBrush)Control<TextBlock>(w,"Diagnostic"+key).Foreground).Color==expected && ((SolidColorBrush)Control<Border>(w,"Diagnostic"+key+"Status").Background).Color==expected,"1.5 diagnostic text and status dot share threshold color "+Themes.Current+" "+key+" "+fraction);
                        }
                    }
                    finally {current.SetValue(controller.Health,actual);}
                    Check(new ProfileStore(folder).Load(controller.Topology).Theme==Themes.Current,"1.5 selected theme is saved on disk "+Themes.Current);
                }
                Press(w,"SettingsNav"); Control<ComboBox>(w,"ThemeSelector").SelectedIndex=0; Pump(); w=MainWindow(app); w.Close();
            }
        }
        private sealed class EditorLifetime : IDisposable
        {
            internal readonly ProfileEditor Window;
            internal EditorLifetime(ProfileEditor window) { Window=window; }
            public void Dispose() {Window.Close();}
        }
        private static Point PaintedCenter(Window window, FrameworkElement text)
        {
            window.UpdateLayout(); Pump();
            var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(Math.Max(1,(int)Math.Ceiling(text.ActualWidth)),Math.Max(1,(int)Math.Ceiling(text.ActualHeight)),96,96,PixelFormats.Pbgra32); bitmap.Render(text);
            DrawingGroup drawing=VisualTreeHelper.GetDrawing(text); if(drawing==null || drawing.Bounds.IsEmpty) throw new Exception("Missing actual text drawing: "+text.Name);
            Rect ink=drawing.Bounds; return text.TranslatePoint(new Point(ink.X+ink.Width/2,ink.Y+ink.Height/2),window);
        }
        private static double TextBaseline(Window window,TextBlock text)
        {
            PaintedCenter(window,text);return text.TranslatePoint(new Point(0,text.BaselineOffset),window).Y;
        }
        private static Point RasterCenter(Window window, FrameworkElement text, double scale)
        {
            Rect bounds=RasterBounds(window,text,scale);return new Point(bounds.X+bounds.Width/2,bounds.Y+bounds.Height/2);
        }
        private static Rect RasterBounds(Window window,FrameworkElement text,double scale)
        {
            PaintedCenter(window,text); DrawingGroup drawing=VisualTreeHelper.GetDrawing(text); var copy=new DrawingVisual();
            Point origin=text.TranslatePoint(new Point(),window),x=text.TranslatePoint(new Point(1,0),window),y=text.TranslatePoint(new Point(0,1),window);
            var matrix=new Matrix(x.X-origin.X,x.Y-origin.Y,y.X-origin.X,y.Y-origin.Y,origin.X,origin.Y);
            Rect actual=new MatrixTransform(matrix).TransformBounds(drawing.Bounds); double left=Math.Floor(actual.Left*scale)/scale-2,top=Math.Floor(actual.Top*scale)/scale-2;
            using(DrawingContext dc=copy.RenderOpen()){dc.PushTransform(new TranslateTransform(-left,-top));dc.PushTransform(new MatrixTransform(matrix));dc.DrawDrawing(drawing);dc.Pop();dc.Pop();}
            int width=Math.Max(1,(int)Math.Ceiling((actual.Right-left+2)*scale)),height=Math.Max(1,(int)Math.Ceiling((actual.Bottom-top+2)*scale));
            var image=new System.Windows.Media.Imaging.RenderTargetBitmap(width,height,96*scale,96*scale,PixelFormats.Pbgra32);image.Render(copy);
            byte[] data=new byte[width*height*4];image.CopyPixels(data,width*4,0);int minX=width,minY=height,maxX=-1,maxY=-1;
            for(int row=0;row<height;row++)for(int column=0;column<width;column++)if(data[(row*width+column)*4+3]>=64){minX=Math.Min(minX,column);maxX=Math.Max(maxX,column);minY=Math.Min(minY,row);maxY=Math.Max(maxY,row);}
            if(maxY<0)throw new Exception("No rasterized glyphs: "+text.Name);
            return new Rect(left+minX/scale,top+minY/scale,(maxX-minX+1)/scale,(maxY-minY+1)/scale);
        }
        private static IEnumerable<string> GlyphFonts(Drawing drawing)
        {
            var glyph=drawing as GlyphRunDrawing;
            if(glyph!=null) yield return glyph.GlyphRun.GlyphTypeface.FontUri.ToString();
            var group=drawing as DrawingGroup;
            if(group!=null) foreach(Drawing child in group.Children) foreach(string font in GlyphFonts(child)) yield return font;
        }
        private static double Contrast(string first,string second)
        {
            Func<string,double> light=value=>{Color c=(Color)ColorConverter.ConvertFromString(value); Func<byte,double> channel=b=>{double s=b/255.0;return s<=0.04045?s/12.92:Math.Pow((s+0.055)/1.055,2.4);};return 0.2126*channel(c.R)+0.7152*channel(c.G)+0.0722*channel(c.B);};
            double firstLight=light(first),secondLight=light(second);return (Math.Max(firstLight,secondLight)+0.05)/(Math.Min(firstLight,secondLight)+0.05);
        }
    }
}
