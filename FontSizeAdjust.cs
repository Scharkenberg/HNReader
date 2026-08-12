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
		// Attached property: global delta in points to add to each element's original font size.
		public static readonly DependencyProperty GlobalDeltaProperty =
			DependencyProperty.RegisterAttached(
				"GlobalDelta",
				typeof(double),
				typeof(FontSizeAdjust),
				new PropertyMetadata(0.0, OnGlobalDeltaChanged));

		// Internal attached property to store original font size per element
		private static readonly DependencyProperty OriginalFontSizeProperty =
			DependencyProperty.RegisterAttached(
				"OriginalFontSize",
				typeof(double?),
				typeof(FontSizeAdjust),
				new PropertyMetadata(null));

		// Public API
		public static void SetGlobalDelta(DependencyObject obj, double value) =>
			obj.SetValue(GlobalDeltaProperty, value);

		public static double GetGlobalDelta(DependencyObject obj) =>
			(double)obj.GetValue(GlobalDeltaProperty);

		// Internal helpers for OriginalFontSize
		private static void SetOriginalFontSize(DependencyObject obj, double? value) =>
			obj.SetValue(OriginalFontSizeProperty, value);

		private static double? GetOriginalFontSize(DependencyObject obj) =>
			(double?)obj.GetValue(OriginalFontSizeProperty);

		private static void OnGlobalDeltaChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			// d is the element where GlobalDelta was set (typically the Window or root panel).
			if (d is FrameworkElement root)
			{
				// Walk the visual tree and apply the delta
				ApplyDeltaToTree(root, (double)e.NewValue);
				// Also attach to future loaded children if needed
				root.Loaded -= Root_Loaded;
				root.Loaded += Root_Loaded;
			}
		}

		private static void Root_Loaded(object? sender, RoutedEventArgs e)
		{
			if (sender is FrameworkElement root)
			{
				// Re-apply in case new children were added after initial set
				var delta = GetGlobalDelta(root);
				ApplyDeltaToTree(root, delta);
			}
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
				}
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine($"ApplyToElementIfHasFontSize error on {current?.GetType().FullName}: {ex}");
				}

				if (current is RichTextBlock rtb)
				{
					try { ApplyToRichTextBlockInlines(rtb, delta); }
					catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"RTB inline error: {ex}"); }
				}

				int count = 0;
				try
				{
					count = VisualTreeHelper.GetChildrenCount(current);
				}
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine($"GetChildrenCount failed for {current?.GetType().FullName}: {ex}");
					continue;
				}

				for (int i = 0; i < count; i++)
				{
					try
					{
						var child = VisualTreeHelper.GetChild(current, i);
						if (child != null) queue.Enqueue(child);
					}
					catch (Exception ex)
					{
						System.Diagnostics.Debug.WriteLine($"GetChild({i}) failed for {current?.GetType().FullName}: {ex}");
					}
				}
			}
		}

		private static void ApplyToElementIfHasFontSize(DependencyObject obj, double delta)
		{
			if (obj == null) return;

			try
			{
				// Controls (Button, TextBox, etc.) inherit from Control and have FontSize
				if (obj is Control ctrl)
				{
					EnsureOriginalAndApply(ctrl, ctrl.FontSize, newSize => ctrl.FontSize = newSize, delta);
					return;
				}

				// TextBlock has FontSize but is not Control
				if (obj is TextBlock tb)
				{
					EnsureOriginalAndApply(tb, tb.FontSize, newSize => tb.FontSize = newSize, delta);
					return;
				}

				// RichTextBlock has FontSize property too (applies to its content)
				if (obj is RichTextBlock rtb)
				{
					EnsureOriginalAndApply(rtb, rtb.FontSize, newSize => rtb.FontSize = newSize, delta);
					return;
				}

				// Runs and Spans are handled when walking inlines (not here)
				// Paragraphs are handled in ApplyToRichTextBlockInlines

				// If we reach here, do not attempt arbitrary GetValue(Control.FontSizeProperty)
				// because some WinRT objects can throw when accessed this way.
			}
			catch (System.Runtime.InteropServices.COMException comEx)
			{
				// Defensive: log and skip this element (prevents app crash)
				System.Diagnostics.Debug.WriteLine($"ApplyToElementIfHasFontSize COMException on {obj?.GetType().FullName}: {comEx.HResult:X} {comEx.Message}");
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"ApplyToElementIfHasFontSize error on {obj?.GetType().FullName}: {ex}");
			}
		}

		private static void EnsureOriginalAndApply(DependencyObject obj, double currentFontSize, Action<double> apply, double delta)
		{
			if (obj == null) return;

			try
			{
				var orig = GetOriginalFontSize(obj);
				if (orig == null)
				{
					// store the baseline the first time we touch this element
					SetOriginalFontSize(obj, currentFontSize);
					orig = currentFontSize;
				}

				var newSize = orig.Value + delta;

				// Defensive numeric checks
				if (double.IsNaN(newSize) || double.IsInfinity(newSize))
				{
					System.Diagnostics.Debug.WriteLine($"FontSizeAdjust: computed invalid font size for {obj.GetType().FullName}: {newSize}");
					return;
				}

				// Clamp to reasonable range
				if (newSize < 6.0) newSize = 6.0;
				if (newSize > 72.0) newSize = 72.0;

				// Apply the computed size
				apply(newSize);
			}
			catch (System.Runtime.InteropServices.COMException comEx)
			{
				// Defensive: some WinRT objects throw when accessed; log and skip
				System.Diagnostics.Debug.WriteLine($"FontSizeAdjust: COMException while adjusting font on {obj.GetType().FullName}: HResult=0x{comEx.HResult:X} Message={comEx.Message}");
			}
			catch (Exception ex)
			{
				// Catch-all so a single problematic element doesn't crash the app
				System.Diagnostics.Debug.WriteLine($"FontSizeAdjust: unexpected error while adjusting font on {obj.GetType().FullName}: {ex}");
			}
		}

		private static void ApplyToRichTextBlockInlines(RichTextBlock rtb, double delta)
		{
			// Walk blocks -> paragraphs -> inlines and adjust Run.FontSize and Span.FontSize if present.
			foreach (var block in rtb.Blocks)
			{
				if (block is Paragraph p)
				{
					// Paragraph.FontSize exists in some frameworks; check and apply if non-zero
					if (p.FontSize > 0)
					{
						EnsureOriginalAndApply(p, p.FontSize, newSize => p.FontSize = newSize, delta);
					}

					foreach (var inline in p.Inlines)
					{
						if (inline is Run run)
						{
							// Runs may have FontSize set explicitly; otherwise they inherit from RichTextBlock/Paragraph.
							var current = run.FontSize;
							if (current <= 0)
							{
								// If run.FontSize is not set, use rtb.FontSize + paragraph.FontSize fallback
								var baseSize = rtb.FontSize;
								if (p.FontSize > 0) baseSize = p.FontSize;
								current = baseSize;
							}
							EnsureOriginalAndApply(run, current, newSize => run.FontSize = newSize, delta);
						}
						else if (inline is Span span)
						{
							if (span.FontSize > 0)
								EnsureOriginalAndApply(span, span.FontSize, newSize => span.FontSize = newSize, delta);
						}
					}
				}
			}
		}

		public static void ReapplyGlobalDelta(FrameworkElement root)
		{
			if (root == null) return;
			// read the delta that was set on this root (or 0 if none)
			var delta = GetGlobalDelta(root);
			// re-walk and apply the delta to the subtree rooted at 'root'
			ApplyDeltaToTree(root, delta);
		}

	}
}
