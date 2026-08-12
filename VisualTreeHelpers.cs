using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.Collections.Generic;

public static class VisualTreeHelpers
{
	public static T? FindDescendant<T>(this DependencyObject root) where T : DependencyObject
	{
		var q = new Queue<DependencyObject>();
		q.Enqueue(root);
		while (q.Count > 0)
		{
			var cur = q.Dequeue();
			var count = VisualTreeHelper.GetChildrenCount(cur);
			for (int i = 0; i < count; i++)
			{
				var child = VisualTreeHelper.GetChild(cur, i);
				if (child is T t) return t;
				q.Enqueue(child);
			}
		}
		return null;
	}
}
