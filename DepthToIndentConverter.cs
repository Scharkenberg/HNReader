// DepthToIndentConverter.cs
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

public partial class DepthToIndentConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, string language)
	{
		int depth = value is int d ? d : 0;
		double baseIndent = 16.0;
		if (parameter != null && double.TryParse(parameter.ToString(), out var p)) baseIndent = p;
		return new Thickness(depth * baseIndent, 0, 0, 0);
	}

	public object ConvertBack(object value, Type targetType, object parameter, string language) =>
		throw new NotSupportedException();
}
