using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HNReader
{
	public static class HtmlRendererHelpers
	{
		public static Func<string, Task>? LinkClickHandler;

		/// <summary>
		/// Render plain text into a RichTextBlock preserving newlines:
		/// - Two-or-more consecutive newlines -> new Paragraph
		/// - Single newline -> LineBreak inside the same Paragraph
		/// Uses the same HtmlDecode + LineBreak logic as the HTML renderer.
		/// </summary>
		public static void RenderPlainTextWithNewlines(RichTextBlock rtb, string? text)
		{
			if (rtb == null) return;
			rtb.Blocks.Clear();

			if (string.IsNullOrEmpty(text))
				return;

			// Normalize CRLF -> \n but do NOT Trim the whole text (we want to preserve single-line content)
			text = text.Replace("\r\n", "\n").Replace("\r", "\n");

			// Split into paragraph chunks on two-or-more newlines (blank line)
			var paragraphParts = Regex.Split(text, @"\n\s*\n");

			foreach (var paraRaw in paragraphParts)
			{
				// keep the raw paragraph chunk (may contain leading/trailing spaces)
				if (string.IsNullOrWhiteSpace(paraRaw))
					continue;

				var paragraph = new Paragraph();

				// Split paragraph into lines on single newline
				var lines = paraRaw.Split(new[] { '\n' }, StringSplitOptions.None);

				for (int i = 0; i < lines.Length; i++)
				{
					var lineRaw = lines[i];

					// Debug so you can see single-line comments hit this path
					System.Diagnostics.Debug.WriteLine($"RenderPlainText text-node RAW: [{lineRaw}]");
					System.Diagnostics.Debug.WriteLine($"RenderPlainText text-node DECODED: [{System.Net.WebUtility.HtmlDecode(lineRaw)}]");

					// Use the same helper that decodes entities and inserts LineBreaks
					// (we call it with the raw line so it can HtmlDecode internally)
					MinimalHtmlRenderer.AppendTextWithLineBreaks(paragraph.Inlines, lineRaw);

					// Note: AppendTextWithLineBreaks will add a LineBreak for internal newlines,
					// but here we add a LineBreak between split lines only if needed.
					if (i < lines.Length - 1)
						paragraph.Inlines.Add(new LineBreak());
				}

				rtb.Blocks.Add(paragraph);
			}
		}
	}

	public static class MinimalHtmlRenderer
	{
		// Very small, safe parser for HN self-text
		private static readonly Regex TagRegex = new Regex(@"</?[^>]+>", RegexOptions.Compiled);
		private static readonly Regex UrlRegex = new Regex(@"https?://[^\s<]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);


		public static void RenderToRichTextBlock(RichTextBlock rtb, string? html)
		{
			rtb.Blocks.Clear();
			if (string.IsNullOrWhiteSpace(html)) return;

			// Remove script/style blocks and dangerous attributes
			html = Regex.Replace(html, @"(?is)<(script|style)[^>]*>.*?</\1>", string.Empty);
			html = Regex.Replace(html, @"on\w+\s*=\s*""[^""]*""", string.Empty, RegexOptions.IgnoreCase);
			html = Regex.Replace(html, @"javascript:", string.Empty, RegexOptions.IgnoreCase);

			// Normalize <br> to newline
			html = Regex.Replace(html, @"(?i)<br\s*/?>", "\n");

			// Treat both opening and closing <p> as paragraph separators.
			html = Regex.Replace(html, @"(?i)<\s*p\b[^>]*>", "\n\n");
			html = Regex.Replace(html, @"(?i)</\s*p\s*>", "\n\n");

			// Split into paragraph chunks on two-or-more newlines
			var paragraphs = Regex.Split(html, @"\n\s*\n");

			foreach (var raw in paragraphs)
			{
				// Keep raw chunk (do not HtmlDecode the whole chunk)
				var chunk = raw;
				if (string.IsNullOrWhiteSpace(chunk)) continue;

				// If chunk contains URLs, split so each URL becomes its own paragraph
				var urlMatches = UrlRegex.Matches(chunk);
				var containsHtmlTags = TagRegex.IsMatch(chunk);

				if (urlMatches.Count == 0 || containsHtmlTags)
				{
					// Normal paragraph: parse tags and text nodes
					var paragraph = new Paragraph();
					int pos = 0;
					foreach (Match m in TagRegex.Matches(chunk))
					{
						var index = m.Index;
						if (index > pos)
						{
							var textRaw = chunk.Substring(pos, index - pos);
							DebugDump("text-node", textRaw);
							AppendTextWithLineBreaks(paragraph.Inlines, textRaw);
						}

						var tag = m.Value;

						// Bold
						if (Regex.IsMatch(tag, @"^<\s*(b|strong)\s*>", RegexOptions.IgnoreCase))
						{
							int close = IndexOfClosingTag(chunk, m.Index + m.Length, new[] { "</b>", "</strong>" });
							if (close >= 0)
							{
								var innerRaw = chunk[(m.Index + m.Length)..close];
								var bold = new Bold();
								DebugDump("text-node", innerRaw);
								AppendTextWithLineBreaks(bold.Inlines, innerRaw);
								paragraph.Inlines.Add(bold);
								pos = close + (chunk.Substring(close).StartsWith("</strong>", StringComparison.OrdinalIgnoreCase) ? 9 : 4);
								continue;
							}
						}

						// Italic
						if (Regex.IsMatch(tag, @"^<\s*(i|em)\s*>", RegexOptions.IgnoreCase))
						{
							int close = IndexOfClosingTag(chunk, m.Index + m.Length, new[] { "</i>", "</em>" });
							if (close >= 0)
							{
								var innerRaw = chunk.Substring(m.Index + m.Length, close - (m.Index + m.Length));
								var italic = new Italic();
								DebugDump("text-node", innerRaw);
								AppendTextWithLineBreaks(italic.Inlines, innerRaw);
								paragraph.Inlines.Add(italic);
								pos = close + (chunk.Substring(close).StartsWith("</em>", StringComparison.OrdinalIgnoreCase) ? 5 : 4);
								continue;
							}
						}

						// Code
						if (Regex.IsMatch(tag, @"^<\s*code\s*>", RegexOptions.IgnoreCase))
						{
							int close = IndexOfClosingTag(chunk, m.Index + m.Length, new[] { "</code>" });
							if (close >= 0)
							{
								var innerRaw = chunk.Substring(m.Index + m.Length, close - (m.Index + m.Length));
								var run = new Run { Text = HtmlDecode(innerRaw) }; // decode code text
								run.FontFamily = new FontFamily("Consolas, 'Courier New', monospace");
								paragraph.Inlines.Add(run);
								pos = close + 7;
								continue;
							}
						}

						// Anchor <a href="...">
						var aOpen = Regex.Match(tag, @"^<\s*a\s+[^>]*href\s*=\s*[""']([^""']+)[""'][^>]*>", RegexOptions.IgnoreCase);
						if (aOpen.Success)
						{
							var rawHref = aOpen.Groups[1].Value;
							var href = HtmlDecode(rawHref); // decode attribute value

							int close = IndexOfClosingTag(chunk, m.Index + m.Length, new[] { "</a>" });
							if (close >= 0)
							{
								var innerRaw = chunk.Substring(m.Index + m.Length, close - (m.Index + m.Length));
								var link = new Hyperlink();

								// capture the href in a local variable (do NOT set NavigateUri)
								var hrefLocal = href;

								DebugDump("text-node", innerRaw);
								AppendTextWithLineBreaks(link.Inlines, innerRaw);

								link.Click += (s, e) =>
								{
									if (!string.IsNullOrEmpty(hrefLocal) && HtmlRendererHelpers.LinkClickHandler != null)
									{
										// fire-and-forget the handler (it will handle HN item links or open externally)
										_ = HtmlRendererHelpers.LinkClickHandler(hrefLocal);
										return;
									}

									// fallback: open externally
									if (Uri.TryCreate(hrefLocal, UriKind.Absolute, out var uri))
									{
										_ = Windows.System.Launcher.LaunchUriAsync(uri);
									}
								};

								paragraph.Inlines.Add(link);
								pos = close + 4;
								continue;
							}
						}

						// fallback: skip tag
						pos = m.Index + m.Length;
					}

					// remaining text
					if (pos < chunk.Length)
					{
						var restRaw = chunk.Substring(pos);
						DebugDump("text-node", restRaw);
						AppendTextWithLineBreaks(paragraph.Inlines, restRaw);
					}

					rtb.Blocks.Add(paragraph);
				}
				else
				{
					// Split chunk into segments around URLs so each URL is its own paragraph
					int lastIndex = 0;
					foreach (Match urlMatch in urlMatches)
					{
						if (urlMatch.Index > lastIndex)
						{
							var before = chunk.Substring(lastIndex, urlMatch.Index - lastIndex);
							if (!string.IsNullOrWhiteSpace(before))
							{
								var pBefore = new Paragraph();
								DebugDump("text-node", before);
								AppendTextWithLineBreaks(pBefore.Inlines, before);
								rtb.Blocks.Add(pBefore);
							}
						}

						var urlText = urlMatch.Value;
						var pUrl = new Paragraph();
						var link = new Hyperlink();

						// capture the decoded URL in a local variable (do NOT set NavigateUri)
						var decodedUrl = HtmlDecode(urlText);

						DebugDump("text-node", urlText);
						AppendTextWithLineBreaks(link.Inlines, urlText);

						link.Click += (s, e) =>
						{
							if (!string.IsNullOrEmpty(decodedUrl) && HtmlRendererHelpers.LinkClickHandler != null)
							{
								_ = HtmlRendererHelpers.LinkClickHandler(decodedUrl);
								return;
							}

							// fallback: open externally
							if (Uri.TryCreate(decodedUrl, UriKind.Absolute, out var uri))
							{
								_ = Windows.System.Launcher.LaunchUriAsync(uri);
							}
						};

						pUrl.Inlines.Add(link);
						rtb.Blocks.Add(pUrl);

						lastIndex = urlMatch.Index + urlMatch.Length;
					}

					if (lastIndex < chunk.Length)
					{
						var after = chunk.Substring(lastIndex);
						if (!string.IsNullOrWhiteSpace(after))
						{
							var pAfter = new Paragraph();
							DebugDump("text-node", after);
							AppendTextWithLineBreaks(pAfter.Inlines, after);
							rtb.Blocks.Add(pAfter);
						}
					}
				}
			}
		}

		/// <summary>
		/// Append text to an InlineCollection, decoding HTML entities, splitting on newlines and inserting LineBreaks.
		/// </summary>
		internal static void AppendTextWithLineBreaks(InlineCollection inlines, string rawText)
		{
			if (string.IsNullOrEmpty(rawText)) return;

			// Decode HTML entities here (handles &#x27;, &quot;, &lt;, &gt;, etc.)
			var text = HtmlDecode(rawText);

			// Normalize CRLF -> \n
			text = text.Replace("\r\n", "\n").Replace("\r", "\n");

			var lines = text.Split(new[] { '\n' }, StringSplitOptions.None);
			for (int i = 0; i < lines.Length; i++)
			{
				var run = new Run { Text = lines[i] };
				inlines.Add(run);
				if (i < lines.Length - 1)
					inlines.Add(new LineBreak());
			}
		}

		private static int IndexOfClosingTag(string text, int startIndex, string[] closingTags)
		{
			int best = -1;
			foreach (var tag in closingTags)
			{
				var idx = text.IndexOf(tag, startIndex, StringComparison.OrdinalIgnoreCase);
				if (idx >= 0 && (best == -1 || idx < best)) best = idx;
			}
			return best;
		}

		private static string HtmlDecode(string s) => System.Net.WebUtility.HtmlDecode(s);

		private static void DebugDump(string label, string raw)
		{
			System.Diagnostics.Debug.WriteLine($"{label} RAW: [{raw}]");
			System.Diagnostics.Debug.WriteLine($"{label} DECODED: [{System.Net.WebUtility.HtmlDecode(raw)}]");
		}

	}
}
