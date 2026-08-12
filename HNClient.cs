using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HNReader
{
	public class HNClient
	{
		// Single shared HttpClient
		private static readonly HttpClient http = new HttpClient(new HttpClientHandler
		{
			AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
			UseProxy = true, // keep default; set to true if you need system proxy
			DefaultProxyCredentials = CredentialCache.DefaultCredentials // optional for corporate proxies
		})
		{
			BaseAddress = new Uri("https://hacker-news.firebaseio.com/v0/"),
			Timeout = TimeSpan.FromSeconds(30),
			DefaultRequestVersion = HttpVersion.Version20,
			DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
		};

		private static readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions
		{
			PropertyNameCaseInsensitive = true
		};

		// Cache for items (Post/CommentRaw) to avoid duplicate network calls
		private readonly ConcurrentDictionary<string, object> _itemCache = new();

		// Limit concurrent outgoing requests to avoid socket exhaustion
		private readonly SemaphoreSlim _throttle;

		// Default concurrency; tuneable (10-50 depending on environment)
		public HNClient(int maxConcurrency = 32)
		{
			_throttle = new SemaphoreSlim(maxConcurrency, maxConcurrency);
		}

		private static async Task<T?> RetryAsync<T>(Func<Task<T?>> action, int maxAttempts, TimeSpan delay) where T : class
		{
			int attempt = 0;
			while (true)
			{
				attempt++;
				try
				{
					return await action().ConfigureAwait(false);
				}
				catch (HttpRequestException) when (attempt < maxAttempts)
				{
					await Task.Delay(delay).ConfigureAwait(false);
					delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 2);
					continue;
				}
				catch
				{
					// Let the caller handle the final exception
					throw;
				}
			}
		}

		// Public API: get top stories (parallelized)
		public async Task<HNResult> GetTopStoriesAsync(int limit = 50)
		{
			try
			{
				// Try to fetch the IDs with a few retries for transient network errors
				var ids = await RetryAsync(() => FetchItemWithThrottleAsync<List<int>>("topstories.json", -1), 3, TimeSpan.FromMilliseconds(10000));
				if (ids == null)
					return HNResult.OnFail("Failed to retrieve story IDs.");

				var take = Math.Min(limit, ids.Count);
				var idSlice = ids.Take(take).ToArray();

				var tasks = idSlice.Select(id => FetchItemWithThrottleAsync<Post>($"item/{id}.json", id)).ToArray();
				var results = await Task.WhenAll(tasks).ConfigureAwait(false);

				var posts = results.Where(p => p != null).ToList()!;
				return HNResult.OnSuccess(posts!);
			}
			catch (HttpRequestException ex)
			{
				// Log details for diagnostics (do not throw)
				System.Diagnostics.Debug.WriteLine($"GetTopStories network error: {ex.Message}; Inner: {ex.InnerException?.Message}");
				return HNResult.OnNetworkFail("No internet connection or server unreachable.");
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"GetTopStories unexpected: {ex}");
				return HNResult.OnFail($"Unexpected error: {ex.Message}");
			}
		}

		public async Task<(Post? Post, List<Comment> Comments)> GetItemWithCommentsAsync(int id, CancellationToken ct = default)
		{
			ct.ThrowIfCancellationRequested();

			var post = await GetItemAsync(id).ConfigureAwait(false);

			if (post == null)
				return (null, new List<Comment>());

			ct.ThrowIfCancellationRequested();

			var comments = await GetCommentsTreeAsync(post).ConfigureAwait(false);

			return (post, comments);
		}
		// Public API: get a single item (Post)
		public async Task<Post?> GetItemAsync(int id)
		{
			var obj = await FetchItemWithThrottleAsync<Post>($"item/{id}.json", id).ConfigureAwait(false);
			return obj;
		}

		// Public API: build comment tree for a post (parallelized)
		public async Task<List<Comment>> GetCommentsTreeAsync(Post post)
		{
			var result = new List<Comment>();
			if (post?.Kids == null || post.Kids.Count == 0) return result;

			// For each top-level kid, BuildCommentNodesAsync may return 0..N nodes
			var tasks = post.Kids.Select(id => BuildCommentNodesAsync(id)).ToArray();
			var lists = await Task.WhenAll(tasks).ConfigureAwait(false);

			foreach (var list in lists)
			{
				if (list != null && list.Count > 0)
					result.AddRange(list);
			}

			return result;
		}

		// --- internal helpers ---

		// Build comment nodes for a given id. Returns 0..N Comment objects.
		// If the raw item is an "empty placeholder" (no by, no text, not dead/deleted),
		// this method will skip that node and return its children instead (promote children).
		private async Task<List<Comment>> BuildCommentNodesAsync(int id)
		{
			var result = new List<Comment>();

			try
			{
				var raw = await FetchItemWithThrottleAsync<CommentRaw>($"item/{id}.json", id).ConfigureAwait(false);
				if (raw == null) return result; // nothing to do

				// Determine flags and heuristics
				bool isDeleted = raw.Deleted == true;
				bool isDead = raw.Dead == true;

				// Normalize text and author for heuristics
				var textRaw = raw.Text ?? string.Empty;
				var byRaw = raw.By ?? string.Empty;

				bool hasText = !string.IsNullOrWhiteSpace(textRaw);
				bool hasBy = !string.IsNullOrWhiteSpace(byRaw);

				// If this item is an explicit deleted/dead item, keep it (placeholder)
				// Otherwise, if it has neither author nor text, treat it as an empty placeholder:
				// skip this node but still build and return its children (promote them).
				bool isEmptyPlaceholder = !isDeleted && !isDead && !hasText && !hasBy;

				// Build children (if any) in parallel
				List<Comment> builtChildren = new List<Comment>();
				if (raw.Kids != null && raw.Kids.Count > 0)
				{
					var childTasks = raw.Kids.Select(BuildCommentNodesAsync).ToArray();
					var childLists = await Task.WhenAll(childTasks).ConfigureAwait(false);
					foreach (var cl in childLists)
						if (cl != null && cl.Count > 0)
							builtChildren.AddRange(cl);
				}

				// Use official API descendants count when available; otherwise fall back to builtChildren.Count
				int officialDescendants = raw.Descendants ?? builtChildren.Count;

				if (isEmptyPlaceholder)
				{
					// Optional debug: log using official descendants value
					System.Diagnostics.Debug.WriteLine($"Skipping empty comment id={id}, API descendants={officialDescendants}, promoted children={builtChildren.Count}");

					// Skip this node, but return its children (promote them)
					return builtChildren;
				}

				// Otherwise create a Comment node (deleted/dead flags copied)
				var node = new Comment
				{
					Id = raw.Id,
					By = raw.By,
					Text = raw.Text ?? string.Empty,
					Time = raw.Time,
					Children = builtChildren,
					Deleted = raw.Deleted ?? false,
					Dead = raw.Dead ?? false,
					Descendants = officialDescendants,
					Score = raw.Score
				};

				result.Add(node);
				return result;
			}
			catch
			{
				// On any error, return empty list (caller will ignore)
				return result;
			}
		}

		// Generic fetch with throttle + caching
		private async Task<T?> FetchItemWithThrottleAsync<T>(string path, int id) where T : class
		{
			// The same HN item may legitimately be deserialized as different
			// CLR types (Post, CommentRaw, HnItemRaw). The cache key therefore
			// must include the requested type.
			var cacheKey = $"{typeof(T).FullName}:{id}";

			if (_itemCache.TryGetValue(cacheKey, out var cached))
			{
				return cached as T;
			}

			await _throttle.WaitAsync().ConfigureAwait(false);

			try
			{
				// Double-check after acquiring the throttle.
				if (_itemCache.TryGetValue(cacheKey, out cached))
				{
					return cached as T;
				}

				var obj = await GetAsync<T>(path).ConfigureAwait(false);

				if (obj != null)
				{
					_itemCache.TryAdd(cacheKey, obj);
				}

				return obj;
			}
			catch
			{
				return null;
			}
			finally
			{
				_throttle.Release();
			}
		}
		private async Task<T?> GetAsync<T>(string path)
		{
			try
			{
				using var resp = await http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
				if (!resp.IsSuccessStatusCode)
				{
					System.Diagnostics.Debug.WriteLine($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} for {path}");
					return default;
				}

				await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
				return await JsonSerializer.DeserializeAsync<T>(stream, jsonOptions).ConfigureAwait(false);
			}
			catch (HttpRequestException ex)
			{
				// Walk the exception chain using Exception type
				Exception? e = ex;
				while (e != null)
				{
					System.Diagnostics.Debug.WriteLine($"HttpRequestException chain: {e.GetType().FullName}: {e.Message}");
					e = e.InnerException;
				}
				throw;
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"GetAsync unexpected error for {path}: {ex.GetType().FullName}: {ex.Message}");
				throw;
			}
		}

		// Build comment node recursively but parallelize children
		private async Task<Comment?> BuildCommentNodeAsync(int id)
		{
			try
			{
				var raw = await FetchItemWithThrottleAsync<CommentRaw>($"item/{id}.json", id).ConfigureAwait(false);
				if (raw == null) return null;

				var node = new Comment
				{
					Id = raw.Id,
					By = raw.By,
					Text = raw.Text ?? string.Empty,
					Time = raw.Time,
					Children = new List<Comment>(),
					Descendants = raw.Descendants ?? 0,

					// copy API flags (null -> false)
					Deleted = raw.Deleted ?? false,
					Dead = raw.Dead ?? false
				};

				if (raw.Kids != null && raw.Kids.Count > 0)
				{
					// Build children in parallel with bounded concurrency
					var childTasks = raw.Kids.Select(BuildCommentNodeAsync).ToArray();
					var children = await Task.WhenAll(childTasks).ConfigureAwait(false);
					foreach (var c in children)
						if (c != null) node.Children.Add(c);
				}

				return node;
			}
			catch
			{
				return null;
			}
		}

		private sealed class HnItemRaw
		{
			public int Id { get; set; }
			public string? Type { get; set; }
			public string? By { get; set; }
			public string? Text { get; set; }
			public string? Title { get; set; }
			public string? Url { get; set; }
			public int? Score { get; set; }
			public long Time { get; set; }
			public int? Parent { get; set; }
			public List<int>? Kids { get; set; }
			public int? Descendants { get; set; }
			public bool? Dead { get; set; }
			public bool? Deleted { get; set; }
		}
		public async Task<(string? Type, int Id, int? Parent)> GetItemInfoAsync(int id,	CancellationToken ct = default)
		{
			ct.ThrowIfCancellationRequested();

			var raw = await FetchItemWithThrottleAsync<HnItemRaw>(
				$"item/{id}.json",
				id).ConfigureAwait(false);

			if (raw == null)
				return (null, id, null);

			return (raw.Type, raw.Id, raw.Parent);
		}

		public async Task<(Post? Story, int? CommentId)> GetStoryContextAsync(int itemId, CancellationToken ct = default)
		{
			var currentId = itemId;

			while (true)
			{
				ct.ThrowIfCancellationRequested();

				var raw = await FetchItemWithThrottleAsync<HnItemRaw>(
					$"item/{currentId}.json",
					currentId).ConfigureAwait(false);

				if (raw == null)
					return (null, null);

				if (string.Equals(
					raw.Type,
					"story",
					StringComparison.OrdinalIgnoreCase))
				{
					var story = await GetItemAsync(raw.Id).ConfigureAwait(false);

					return (story, itemId == raw.Id ? null : itemId);
				}

				if (!string.Equals(
					raw.Type,
					"comment",
					StringComparison.OrdinalIgnoreCase) ||
					!raw.Parent.HasValue)
				{
					return (null, null);
				}

				currentId = raw.Parent.Value;
			}
		}

		// Raw shape for comment deserialization
		private class CommentRaw
		{
			public int Id { get; set; }
			public string? By { get; set; }
			public string? Text { get; set; }
			public long Time { get; set; }
			public List<int>? Kids { get; set; }
			public int? Descendants { get; set; }
			public int? Score { get; set; }
			// HN API flags
			public bool? Deleted { get; set; }
			public bool? Dead { get; set; }
		}

		// Public streaming API: yields top-level Comment nodes as they are built.
		// Uses existing BuildCommentNodesAsync internally.
		public async IAsyncEnumerable<Comment> GetCommentsStreamAsync(Post post, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
		{
			if (post?.Kids == null) yield break;

			foreach (var kidId in post.Kids)
			{
				ct.ThrowIfCancellationRequested();
				List<Comment> built;
				try
				{
					// BuildCommentNodesAsync is already implemented and returns 0..N Comment objects for an id
					built = await BuildCommentNodesAsync(kidId).ConfigureAwait(false);
				}
				catch
				{
					built = new List<Comment>();
				}

				foreach (var c in built)
				{
					ct.ThrowIfCancellationRequested();
					yield return c;
				}
			}
		}
	}


	public class Comment
	{
		public int Id { get; set; }
		public bool Deleted { get; set; } = false;
		public bool Dead { get; set; } = false;
		public string? By { get; set; }
		public string Text { get; set; } = string.Empty;
		public long Time { get; set; }
		public List<Comment> Children { get; set; } = new List<Comment>();
		public int Descendants { get; set; } = 0;
		public int? Score { get; set; }
		public bool IsExpanded { get; set; } = true;
		public string TimeAgo
		{
			get
			{
				var ts = DateTimeOffset.FromUnixTimeSeconds(Time).DateTime;
				var span = DateTime.UtcNow - ts.ToUniversalTime();
				if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d ago";
				if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h ago";
				if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m ago";
				return "just now";
			}
		}
		public string ScoreAndTime
		{
			get
			{
				if (Score.HasValue && Score.Value != 0)
				{
					// show plus sign for positive points; adjust wording if you prefer "5 points"
					return $"+{Score.Value} · {TimeAgo}";
				}
				// no score available or zero -> show only time
				return TimeAgo;
			}
		}
	}

	public class HNResult
	{
		public bool Success { get; private set; }
		public bool IsNetworkError { get; private set; }
		public string? ErrorMessage { get; private set; }
		public List<Post> Posts { get; private set; } = new();

		private HNResult() { }

		public static HNResult OnSuccess(List<Post>? posts) =>
			new HNResult { Success = true, Posts = posts! };

		public static HNResult OnFail(string message) =>
			new HNResult { Success = false, ErrorMessage = message };

		public static HNResult OnNetworkFail(string message) =>
			new HNResult { Success = false, IsNetworkError = true, ErrorMessage = message };

	}

	public class Post
	{
		public int Id { get; set; }
		public string? Title { get; set; }
		public string? By { get; set; }
		public int Score { get; set; }
		public long Time { get; set; }
		public string? Url { get; set; }
		public List<int>? Kids { get; set; }
		public int Descendants { get; set; } = 0;
		public string? Text { get; set; }    // HN "text" field for self posts (may be null)
		public bool HasText => !string.IsNullOrWhiteSpace(Text);

		public DateTime Timestamp =>
			DateTimeOffset.FromUnixTimeSeconds(Time).DateTime;

		public string ScoreText => $"{Score} points";

		public string CommentCountText => $"{Descendants} comments";

		public string TimeAgo
		{
			get
			{
				var span = DateTime.UtcNow - Timestamp.ToUniversalTime();
				if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d ago";
				if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h ago";
				if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m ago";
				return "just now";
			}
		}

		public string DisplayUrl
		{
			get
			{
				if (string.IsNullOrWhiteSpace(Url)) return "self";
				try
				{
					var uri = new Uri(Url);
					return uri.Host.Replace("www.", "");
				}
				catch
				{
					return Url;
				}
			}
		}
	}
}
