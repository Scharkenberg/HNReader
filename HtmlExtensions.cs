using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace HNReader
{

	public static class HtmlExtensions
	{
		public static readonly DependencyProperty HtmlProperty =
			DependencyProperty.RegisterAttached(
				"Html",
				typeof(string),
				typeof(HtmlExtensions),
				new PropertyMetadata(null, OnHtmlChanged));

		public static void SetHtml(DependencyObject element, string value) =>
			element.SetValue(HtmlProperty, value);

		public static string GetHtml(DependencyObject element) =>
			(string)element.GetValue(HtmlProperty);

		private static void OnHtmlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			if (d is not RichTextBlock rtb) return;

			var html = e.NewValue as string ?? string.Empty;
			System.Diagnostics.Debug.WriteLine($"OnHtmlChanged called. Length={html.Length}. LooksLikeHtml={LooksLikeHtml(html)}");

			rtb.DispatcherQueue.TryEnqueue(() =>
			{
				rtb.Blocks.Clear();
				if (string.IsNullOrWhiteSpace(html)) return;

				if (LooksLikeHtml(html))
				{
					System.Diagnostics.Debug.WriteLine("Using HTML renderer");
					try { MinimalHtmlRenderer.RenderToRichTextBlock(rtb, html); }
					catch { HtmlRendererHelpers.RenderPlainTextWithNewlines(rtb, html); }
				}
				else
				{
					System.Diagnostics.Debug.WriteLine("Using plain-text renderer");
					HtmlRendererHelpers.RenderPlainTextWithNewlines(rtb, html);
				}
			});
		}

		internal static bool LooksLikeHtml(string s)
		{
			if (string.IsNullOrWhiteSpace(s)) return false;
			// Simple heuristic: presence of angle brackets with letters inside
			return s.Contains("<") && s.Contains(">");
		}

		// Call: RenderComment(rtb, commentModel);
		public static void RenderComment(RichTextBlock rtb, Comment comment)
		{
			rtb.Blocks.Clear();

			// Normalize text for heuristics
			var rawText = comment?.Text ?? string.Empty;
			var decoded = System.Net.WebUtility.HtmlDecode(rawText ?? string.Empty).Trim();

			// API flags (adjust property names to your model)
			bool isDeleted = comment?.Deleted == true;
			bool isDead = comment?.Dead == true;

			// Heuristic fallback for HN-style markers
			bool looksLikeMarker = string.IsNullOrWhiteSpace(decoded)
								   || string.Equals(decoded, "[dead]", StringComparison.OrdinalIgnoreCase)
								   || string.Equals(decoded, "[deleted]", StringComparison.OrdinalIgnoreCase);

			if (isDeleted || isDead || looksLikeMarker)
			{
				// Choose message priority: Deleted > Dead > Flagged > Generic
				string message;
				if (isDeleted) message = "Comment deleted";
				else if (isDead) message = "Comment removed (dead)";
				else message = "Comment removed";

				var p = new Paragraph();
				var run = new Run { Text = message };
				// Visual styling: subdued and italic
				run.FontStyle = Windows.UI.Text.FontStyle.Italic;
				run.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemControlForegroundBaseLowBrush"];
				p.Inlines.Add(run);

				// Optional: small metadata (who/when) if available
				if (!string.IsNullOrEmpty(comment?.By))
				{
					p.Inlines.Add(new LineBreak());
					var meta = new Run { Text = $"— {comment.By}" };
					meta.FontSize = 12;
					meta.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemControlForegroundBaseLowBrush"];
					p.Inlines.Add(meta);
				}

				rtb.Blocks.Add(p);
				return;
			}

			// Otherwise render normally (use your existing logic)
			if (LooksLikeHtml(decoded))
				MinimalHtmlRenderer.RenderToRichTextBlock(rtb, rawText);
			else
				HtmlRendererHelpers.RenderPlainTextWithNewlines(rtb, rawText);
		}

	}
	public static class Debounce
	{
		// Call Debounce.Run(() => DoWork(), 200, ref _cts);
		public static async Task Run(Func<Task> action, int delayMs, CancellationTokenSource? previousCts = null)
		{
			previousCts?.Cancel();
			var cts = new CancellationTokenSource();
			try
			{
				await Task.Delay(delayMs, cts.Token).ConfigureAwait(false);
				await action().ConfigureAwait(false);
			}
			catch (OperationCanceledException) { }
			finally
			{
				previousCts?.Dispose();
				// caller should keep reference if needed
			}
		}
	}
}
