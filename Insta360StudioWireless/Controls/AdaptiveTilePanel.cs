using System;
using System.Windows;
using System.Windows.Controls;

namespace Insta360StudioWireless.Controls
{
    /// <summary>
    /// Responsive adaptive grid panel that dynamically scales tiles so they fill 
    /// the full width of the gallery container without leaving empty gaps on the right.
    /// </summary>
    public class AdaptiveTilePanel : Panel
    {
        public static readonly DependencyProperty MinItemWidthProperty =
            DependencyProperty.Register(
                nameof(MinItemWidth),
                typeof(double),
                typeof(AdaptiveTilePanel),
                new FrameworkPropertyMetadata(240.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty AspectRatioProperty =
            DependencyProperty.Register(
                nameof(AspectRatio),
                typeof(double),
                typeof(AdaptiveTilePanel),
                new FrameworkPropertyMetadata(0.67, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty SpacingProperty =
            DependencyProperty.Register(
                nameof(Spacing),
                typeof(double),
                typeof(AdaptiveTilePanel),
                new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public double MinItemWidth
        {
            get => (double)GetValue(MinItemWidthProperty);
            set => SetValue(MinItemWidthProperty, value);
        }

        public double AspectRatio
        {
            get => (double)GetValue(AspectRatioProperty);
            set => SetValue(AspectRatioProperty, value);
        }

        public double Spacing
        {
            get => (double)GetValue(SpacingProperty);
            set => SetValue(SpacingProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            if (InternalChildren.Count == 0)
                return new Size(0, 0);

            double width = availableSize.Width;
            if (double.IsPositiveInfinity(width) || double.IsNaN(width) || width <= 0)
            {
                width = MinItemWidth;
            }

            double spacing = Spacing;
            double minWidth = Math.Max(50.0, MinItemWidth);

            int cols = Math.Max(1, (int)Math.Floor((width + spacing) / (minWidth + spacing)));
            double itemWidth = (width - (cols - 1) * spacing) / cols;
            if (itemWidth < minWidth && cols > 1)
            {
                cols = Math.Max(1, cols - 1);
                itemWidth = (width - (cols - 1) * spacing) / cols;
            }

            double itemHeight = Math.Round(itemWidth * AspectRatio);
            Size childSize = new Size(itemWidth, itemHeight);

            foreach (UIElement child in InternalChildren)
            {
                child?.Measure(childSize);
            }

            int count = InternalChildren.Count;
            int rows = (count + cols - 1) / cols;
            double totalHeight = rows * itemHeight + (rows > 1 ? (rows - 1) * spacing : 0);

            return new Size(width, totalHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (InternalChildren.Count == 0)
                return finalSize;

            double width = finalSize.Width;
            double spacing = Spacing;
            double minWidth = Math.Max(50.0, MinItemWidth);

            int cols = Math.Max(1, (int)Math.Floor((width + spacing) / (minWidth + spacing)));
            double itemWidth = (width - (cols - 1) * spacing) / cols;
            if (itemWidth < minWidth && cols > 1)
            {
                cols = Math.Max(1, cols - 1);
                itemWidth = (width - (cols - 1) * spacing) / cols;
            }

            double itemHeight = Math.Round(itemWidth * AspectRatio);

            for (int i = 0; i < InternalChildren.Count; i++)
            {
                UIElement child = InternalChildren[i];
                if (child == null) continue;

                int col = i % cols;
                int row = i / cols;

                double x = col * (itemWidth + spacing);
                double y = row * (itemHeight + spacing);

                // Snap the last item in a full row to the exact panel width to avoid subpixel rounding gaps
                double currentItemWidth = (col == cols - 1) ? Math.Max(0, width - x) : itemWidth;

                child.Arrange(new Rect(x, y, currentItemWidth, itemHeight));
            }

            return finalSize;
        }
    }
}
