using System;
using System.Windows;
using System.Windows.Controls;

namespace NumaControl
{
    // A single typographic row. Different fonts share a baseline without render transforms.
    public sealed class BaselinePanel : Panel
    {
        private double baseline;
        private static double Baseline(UIElement child)
        {
            var text = child as TextBlock;
            return text == null || double.IsNaN(text.BaselineOffset) ? child.DesiredSize.Height : text.BaselineOffset;
        }
        protected override Size MeasureOverride(Size availableSize)
        {
            double width = 0, descent = 0; baseline = 0;
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double ascent = Baseline(child); baseline = Math.Max(baseline, ascent);
                descent = Math.Max(descent, child.DesiredSize.Height - ascent); width += child.DesiredSize.Width;
            }
            return new Size(width, baseline + descent);
        }
        protected override Size ArrangeOverride(Size finalSize)
        {
            double x = 0;
            foreach (UIElement child in InternalChildren)
            {
                child.Arrange(new Rect(x, baseline - Baseline(child), child.DesiredSize.Width, child.DesiredSize.Height));
                x += child.DesiredSize.Width;
            }
            return finalSize;
        }
    }
}
