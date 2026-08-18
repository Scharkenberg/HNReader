using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace HNReader
{
	public static class FontSizeAdjust
	{
		public static readonly DependencyProperty GlobalDeltaProperty =
			DependencyProperty.RegisterAttached(
				"GlobalDelta",
				typeof(double),
				typeof(FontSizeAdjust),
				new PropertyMetadata(0.0, OnGlobalDeltaChanged));

		private static readonly DependencyProperty OriginalFontSizeProperty =
			DependencyProperty.RegisterAttached(
				"OriginalFontSize",
				typeof(double?),
				typeof(FontSizeAdjust),
				new PropertyMetadata(null));

		public static void SetGlobalDelta(DependencyObject obj, double value) =>
			obj.SetValue(GlobalDeltaProperty, value);

		public static double GetGlobalDelta(DependencyObject obj) =>
			(double)obj.GetValue(GlobalDeltaProperty);

		private static void SetOriginalFontSize(DependencyObject obj, double? value) =>
			obj.SetValue(OriginalFontSizeProperty, value);

		private static double? GetOriginalFontSize(DependencyObject obj) =>
			(double?)obj.GetValue(OriginalFontSizeProperty);

		private static void OnGlobalDeltaChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			if (d is FrameworkElement root)
				ApplyDeltaToTree(root, (double)e.NewValue);
		}

		private static void ApplyDeltaToTree(FrameworkElement root, double delta)
		{
			var queue = new Queue<DependencyObject>();
			queue.Enqueue(root);

			while (queue.Count > 0)
			{
				var current = queue.Dequeue();

				try
				{
					ApplyToElementIfHasFontSize(current, delta);

					if (current is RichTextBlock rtb)
						ApplyToRichTextBlockInlines(rtb, delta);
				}
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine(
						$"FontSizeAdjust error on {current.GetType().FullName}: {ex.Message}");
				}

				int count;
				try { count = VisualTreeHelper.GetChildrenCount(current); }
				catch { continue; }

				for (int i = 0; i < count; i++)
				{
					try
					{
						var child = VisualTreeHelper.GetChild(current, i);
						if (child != null)
							queue.Enqueue(child);
					}
					catch { }
				}
			}
		}

		private static void ApplyToElementIfHasFontSize(DependencyObject obj, double delta)
		{
			try
			{
				if (obj is Control ctrl)
				{
					EnsureOriginalAndApply(ctrl, ctrl.FontSize, n => ctrl.FontSize = n, delta);
					return;
				}

				if (obj is TextBlock tb)
				{
					EnsureOriginalAndApply(tb, tb.FontSize, n => tb.FontSize = n, delta);
					return;
				}

				if (obj is RichTextBlock rtb)
					EnsureOriginalAndApply(rtb, rtb.FontSize, n => rtb.FontSize = n, delta);
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine(
					$"FontSizeAdjust element error: {ex.Message}");
			}
		}

		private static void EnsureOriginalAndApply(
			DependencyObject obj,
			double currentFontSize,
			Action<double> apply,
			double delta)
		{
			var original = GetOriginalFontSize(obj);
			if (!original.HasValue)
			{
				SetOriginalFontSize(obj, currentFontSize);
				original = currentFontSize;
			}

			var newSize = Math.Clamp(original.Value + delta, 6.0, 72.0);
			if (!double.IsNaN(newSize) && !double.IsInfinity(newSize))
				apply(newSize);
		}

		private static void ApplyToRichTextBlockInlines(RichTextBlock rtb, double delta)
		{
			foreach (var block in rtb.Blocks)
			{
				if (block is not Paragraph paragraph)
					continue;

				if (paragraph.FontSize > 0)
				{
					EnsureOriginalAndApply(
						paragraph,
						paragraph.FontSize,
						n => paragraph.FontSize = n,
						delta);
				}

				foreach (var inline in paragraph.Inlines)
				{
					if (inline is Run run)
					{
						var current = run.FontSize;
						if (current <= 0)
							current = paragraph.FontSize > 0 ? paragraph.FontSize : rtb.FontSize;

						EnsureOriginalAndApply(run, current, n => run.FontSize = n, delta);
					}
					else if (inline is Span span && span.FontSize > 0)
					{
						EnsureOriginalAndApply(span, span.FontSize, n => span.FontSize = n, delta);
					}
				}
			}
		}

		public static void ApplyToElement(FrameworkElement element, double delta)
		{
			if (element == null)
				return;

			ApplyToElementIfHasFontSize(element, delta);

			if (element is RichTextBlock rtb)
				ApplyToRichTextBlockInlines(rtb, delta);
		}

		public static void ReapplyGlobalDelta(FrameworkElement root)
		{
			if (root == null)
				return;

			ApplyDeltaToTree(root, GetGlobalDelta(root));
		}
	}
}
