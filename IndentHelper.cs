using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HNReader
{
	public static class IndentAdjust
	{
		public static readonly DependencyProperty GlobalIndentDeltaProperty =
			DependencyProperty.RegisterAttached(
				"GlobalIndentDelta",
				typeof(double),
				typeof(IndentAdjust),
				new PropertyMetadata(0.0, OnGlobalIndentDeltaChanged));

		private static readonly DependencyProperty OriginalMarginProperty =
			DependencyProperty.RegisterAttached(
				"OriginalMargin",
				typeof(Thickness?),
				typeof(IndentAdjust),
				new PropertyMetadata(null));

		private static readonly DependencyProperty OriginalPaddingProperty =
			DependencyProperty.RegisterAttached(
				"OriginalPadding",
				typeof(Thickness?),
				typeof(IndentAdjust),
				new PropertyMetadata(null));

		public static void SetGlobalIndentDelta(DependencyObject obj, double value) =>
			obj.SetValue(GlobalIndentDeltaProperty, value);

		public static double GetGlobalIndentDelta(DependencyObject obj) =>
			(double)obj.GetValue(GlobalIndentDeltaProperty);

		private static void SetOriginalMargin(DependencyObject obj, Thickness? value) =>
			obj.SetValue(OriginalMarginProperty, value);

		private static Thickness? GetOriginalMargin(DependencyObject obj) =>
			(Thickness?)obj.GetValue(OriginalMarginProperty);

		private static void SetOriginalPadding(DependencyObject obj, Thickness? value) =>
			obj.SetValue(OriginalPaddingProperty, value);

		private static Thickness? GetOriginalPadding(DependencyObject obj) =>
			(Thickness?)obj.GetValue(OriginalPaddingProperty);

		private static void OnGlobalIndentDeltaChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			if (d is not FrameworkElement root) return;

			_ = root.DispatcherQueue.TryEnqueue(() =>
			{
				try
				{
					root.Loaded -= Root_Loaded;
					root.Loaded += Root_Loaded;
					ApplyIndentToTree(root, (double)e.NewValue);
				}
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine($"IndentAdjust.OnGlobalIndentDeltaChanged: {ex}");
				}
			});
		}

		private static void Root_Loaded(object? sender, RoutedEventArgs e)
		{
			if (sender is FrameworkElement root)
			{
				var delta = GetGlobalIndentDelta(root);
				ApplyIndentToTree(root, delta);
			}
		}

		private static void ApplyIndentToTree(FrameworkElement root, double delta)
		{
			var queue = new Queue<DependencyObject>();
			queue.Enqueue(root);

			while (queue.Count > 0)
			{
				var current = queue.Dequeue();

				// If this is a TreeViewItem, adjust its header visual
				if (current is TreeViewItem tvi)
				{
					try
					{
						ApplyToTreeViewItem(tvi, delta);
					}
					catch (Exception ex)
					{
						System.Diagnostics.Debug.WriteLine($"IndentAdjust: error adjusting TreeViewItem: {ex}");
					}
				}

				int count = 0;
				try
				{
					count = VisualTreeHelper.GetChildrenCount(current);
				}
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine($"IndentAdjust: GetChildrenCount failed for {current?.GetType().FullName}: {ex}");
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
						System.Diagnostics.Debug.WriteLine($"IndentAdjust: GetChild({i}) failed for {current?.GetType().FullName}: {ex}");
					}
				}
			}
		}

		// New: compute depth by walking up parent chain and then adjust a concrete visual inside the TreeViewItem
		private static void ApplyToTreeViewItem(TreeViewItem tvi, double delta)
		{
			if (tvi == null) return;

			// compute depth by counting ancestor TreeViewItem nodes
			int depth = 0;
			DependencyObject parent = VisualTreeHelper.GetParent(tvi);
			while (parent != null)
			{
				if (parent is TreeViewItem) depth++;
				parent = VisualTreeHelper.GetParent(parent);
			}

			// If depth is 0 (top-level), you may want to skip or use depth-1 mapping; adjust as needed.
			// Compute indent offset: delta * depth
			var indent = delta * depth;

			// Try to find a header presenter (ContentPresenter or first FrameworkElement child) to adjust Margin
			var headerPresenter = FindDescendant<ContentPresenter>(tvi) as FrameworkElement
								  ?? FindDescendant<FrameworkElement>(tvi);

			if (headerPresenter != null)
			{
				// store original margin once
				var origMargin = GetOriginalMargin(headerPresenter);
				if (origMargin == null)
				{
					try
					{
						SetOriginalMargin(headerPresenter, headerPresenter.Margin);
						origMargin = headerPresenter.Margin;
					}
					catch (Exception ex)
					{
						System.Diagnostics.Debug.WriteLine($"IndentAdjust: failed to read headerPresenter.Margin: {ex}");
						origMargin = new Thickness(0);
						SetOriginalMargin(headerPresenter, origMargin);
					}
				}

				var newLeft = origMargin.Value.Left + indent;
				if (double.IsNaN(newLeft) || double.IsInfinity(newLeft)) newLeft = origMargin.Value.Left;
				if (newLeft < 0) newLeft = 0;

				var newMargin = new Thickness(newLeft, origMargin.Value.Top, origMargin.Value.Right, origMargin.Value.Bottom);
				try
				{
					headerPresenter.Margin = newMargin;
				}
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine($"IndentAdjust: failed to set headerPresenter.Margin: {ex}");
				}

				return;
			}

			// Fallback: if no presenter found, try to set Padding on the TreeViewItem itself (if available)
			try
			{
				// store original padding once
				var origPad = GetOriginalPadding(tvi);
				if (origPad == null)
				{
					try
					{
						// TreeViewItem may expose Padding (Control), try to read it
						var padObj = typeof(Control).GetProperty("Padding")?.GetValue(tvi);
						var pad = (padObj is Thickness th) ? th : new Thickness(0);
						SetOriginalPadding(tvi, pad);
						origPad = pad;
					}
					catch { origPad = new Thickness(0); SetOriginalPadding(tvi, origPad); }
				}

				var newLeftPad = origPad.Value.Left + indent;
				if (double.IsNaN(newLeftPad) || double.IsInfinity(newLeftPad)) newLeftPad = origPad.Value.Left;
				if (newLeftPad < 0) newLeftPad = 0;

				var newPad = new Thickness(newLeftPad, origPad.Value.Top, origPad.Value.Right, origPad.Value.Bottom);

				// try to set Padding via reflection to avoid compile-time dependency
				var paddingProp = typeof(Control).GetProperty("Padding");
				if (paddingProp != null && paddingProp.CanWrite)
				{
					paddingProp.SetValue(tvi, newPad);
				}
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"IndentAdjust: fallback padding set failed: {ex}");
			}
		}

		private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
		{
			if (root == null) return null;
			var q = new Queue<DependencyObject>();
			q.Enqueue(root);

			while (q.Count > 0)
			{
				var cur = q.Dequeue();
				if (cur is T t) return t;

				int c = 0;
				try { c = VisualTreeHelper.GetChildrenCount(cur); }
				catch { continue; }

				for (int i = 0; i < c; i++)
				{
					try
					{
						var child = VisualTreeHelper.GetChild(cur, i);
						if (child != null) q.Enqueue(child);
					}
					catch { /* ignore individual child errors */ }
				}
			}

			return null;
		}

		public static void ReapplyGlobalIndent(FrameworkElement root)
		{
			if (root == null) return;
			var delta = GetGlobalIndentDelta(root);
			ApplyIndentToTree(root, delta);
		}
	}
}
