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

		private static readonly DependencyProperty HeaderPresenterProperty =
			DependencyProperty.RegisterAttached(
				"HeaderPresenter",
				typeof(FrameworkElement),
				typeof(IndentAdjust),
				new PropertyMetadata(null));

		private static readonly DependencyProperty DepthProperty =
			DependencyProperty.RegisterAttached(
				"Depth",
				typeof(int?),
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

		private static void SetHeaderPresenter(DependencyObject obj, FrameworkElement? value) =>
			obj.SetValue(HeaderPresenterProperty, value);

		private static FrameworkElement? GetHeaderPresenter(DependencyObject obj) =>
			obj.GetValue(HeaderPresenterProperty) as FrameworkElement;

		private static void SetDepth(DependencyObject obj, int? value) =>
			obj.SetValue(DepthProperty, value);

		private static int? GetDepth(DependencyObject obj) =>
			(int?)obj.GetValue(DepthProperty);

		private static void OnGlobalIndentDeltaChanged(
			DependencyObject d,
			DependencyPropertyChangedEventArgs e)
		{
			if (d is FrameworkElement root)
				ApplyRealizedTree(root, (double)e.NewValue);
		}

		public static void ApplyRealizedTree(FrameworkElement root)
		{
			if (root == null)
				return;

			ApplyRealizedTree(root, GetGlobalIndentDelta(root));
		}

		private static void ApplyRealizedTree(FrameworkElement root, double delta)
		{
			var queue = new Queue<DependencyObject>();
			queue.Enqueue(root);

			while (queue.Count > 0)
			{
				var current = queue.Dequeue();

				if (current is TreeViewItem item)
					ApplyToTreeViewItem(item, delta);

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

		public static void ApplyToTreeViewItem(TreeViewItem item, double delta)
		{
			if (item == null)
				return;

			var depth = GetDepth(item);
			if (!depth.HasValue)
			{
				int computed = 0;
				DependencyObject? parent = VisualTreeHelper.GetParent(item);
				while (parent != null)
				{
					if (parent is TreeViewItem)
						computed++;

					parent = VisualTreeHelper.GetParent(parent);
				}

				SetDepth(item, computed);
				depth = computed;
			}

			var headerPresenter = GetHeaderPresenter(item);
			if (headerPresenter == null)
			{
				headerPresenter = FindDescendant<ContentPresenter>(item)
					?? FindDescendant<FrameworkElement>(item);

				if (headerPresenter != null)
					SetHeaderPresenter(item, headerPresenter);
			}

			var amount = delta * depth.Value;

			if (headerPresenter != null)
			{
				var original = GetOriginalMargin(headerPresenter);
				if (!original.HasValue)
				{
					original = headerPresenter.Margin;
					SetOriginalMargin(headerPresenter, original);
				}

				var newLeft = original.Value.Left + amount;
				if (double.IsNaN(newLeft) || double.IsInfinity(newLeft))
					newLeft = original.Value.Left;

				newLeft = Math.Max(0, newLeft);

				headerPresenter.Margin = new Thickness(
					newLeft,
					original.Value.Top,
					original.Value.Right,
					original.Value.Bottom);

				return;
			}

			try
			{
				var originalPadding = GetOriginalPadding(item);
				if (!originalPadding.HasValue)
				{
					var property = typeof(Control).GetProperty("Padding");
					var padding = property?.GetValue(item) is Thickness th
						? th
						: new Thickness(0);

					SetOriginalPadding(item, padding);
					originalPadding = padding;
				}

				var newLeft = Math.Max(
					0,
					originalPadding.Value.Left + amount);

				var setter = typeof(Control).GetProperty("Padding");
				if (setter?.CanWrite == true)
				{
					setter.SetValue(
						item,
						new Thickness(
							newLeft,
							originalPadding.Value.Top,
							originalPadding.Value.Right,
							originalPadding.Value.Bottom));
				}
			}
			catch { }
		}

		private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
		{
			if (root == null)
				return null;

			var queue = new Queue<DependencyObject>();
			queue.Enqueue(root);

			while (queue.Count > 0)
			{
				var current = queue.Dequeue();
				if (current is T result)
					return result;

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

			return null;
		}

		public static void ReapplyGlobalIndent(FrameworkElement root)
		{
			ApplyRealizedTree(root);
		}
	}
}
