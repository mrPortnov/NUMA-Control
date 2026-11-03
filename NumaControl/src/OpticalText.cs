using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace NumaControl
{
    // Align actual retained glyphs. No polling and no global LayoutUpdated subscription.
    public sealed class OpticalText : TextBlock
    {
        private DispatcherOperation pending;
        static OpticalText()
        {
            foreach (DependencyProperty property in new[] { TextProperty, FontSizeProperty, FontFamilyProperty, FontWeightProperty, FontStyleProperty, FontStretchProperty, LineHeightProperty, TextAlignmentProperty, TextWrappingProperty, TextTrimmingProperty })
                property.OverrideMetadata(typeof(OpticalText), new FrameworkPropertyMetadata(delegate(DependencyObject target, DependencyPropertyChangedEventArgs change) { ((OpticalText)target).QueueAlignment(); }));
        }
        public OpticalText()
        {
            FontFamily = Themes.Font;
            Loaded += delegate { QueueAlignment(); };
            IsVisibleChanged += delegate(object sender, DependencyPropertyChangedEventArgs change) { if ((bool)change.NewValue) QueueAlignment(); };
            Unloaded += delegate { if (pending != null && pending.Status == DispatcherOperationStatus.Pending) pending.Abort(); pending = null; };
        }
        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        { base.OnRenderSizeChanged(sizeInfo); QueueAlignment(); }
        private void QueueAlignment()
        {
            if (!IsLoaded || !IsVisible || (pending != null && pending.Status == DispatcherOperationStatus.Pending)) return;
            pending = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
            {
                pending = null;
                DrawingGroup drawing = VisualTreeHelper.GetDrawing(this);
                if (!IsLoaded || !IsVisible || ActualHeight <= 0 || drawing == null || drawing.Bounds.IsEmpty) return;
                Rect ink = drawing.Bounds; double offset = ActualHeight / 2 - (ink.Top + ink.Height / 2);
                TranslateTransform old = RenderTransform as TranslateTransform;
                if (old == null || Math.Abs(old.Y - offset) > 0.001) RenderTransform = new TranslateTransform(0, offset);
            }));
        }
    }
}
