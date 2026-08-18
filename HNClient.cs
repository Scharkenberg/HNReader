using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HNReader
{
	public class HNClient
	{
		private static readonly HttpClient http = new HttpClient(new HttpClientHandler
		{
			AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
			UseProxy = true,
			DefaultProxyCredentials = CredentialCache.DefaultCredentials
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

		// Completed-result cache. The requested CLR type is part of the key because
		// one HN item can legitimately be deserialized as Post, HnItemRaw, or CommentRaw.
		private readonly ConcurrentDictionary<string, object> _itemCache = new();

		// In-flight request cache. Concurrent callers for the same (type, item) await
		// one HTTP request rather than creating duplicate network traffic.
		private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _itemInFlight = new();

		private readonly SemaphoreSlim _throttle;
		private const int DefaultConcurrency = 32;
		private const int CommentFetchBatchSize = 16;

		public HNClient(int maxConcurrency = DefaultConcurrency)
		{
			if (maxConcurrency < 1)
				throw new ArgumentOutOfRangeException(nameof(maxConcurrency));

			_throttle = new SemaphoreSlim(maxConcurrency, maxConcurrency);
		}

		private static bool IsTransientHttpError(HttpRequestException ex)
		{
			if (!ex.StatusCode.HasValue)
				return true;

			var status = (int)ex.StatusCode.Value;
			return status == 408 || status == 429 || status >= 500;
		}

		private static async Task<T?> RetryAsync<T>(
			Func<CancellationToken, Task<T?>> action,
			CancellationToken ct,
			int maxAttempts = 3,
			TimeSpan? initialDelay = null) where T : class
		{
			var delay = initialDelay ?? TimeSpan.FromMilliseconds(250);

			for (var attempt = 1; ; attempt++)
			{
				ct.ThrowIfCancellationRequested();

				try
				{
					return await action(ct).ConfigureAwait(false);
				}
				catch (HttpRequestException ex) when (attempt < maxAttempts && IsTransientHttpError(ex))
				{
					await Task.Delay(delay, ct).ConfigureAwait(false);
					delay = TimeSpan.FromMilliseconds(Math.Min(3000, delay.TotalMilliseconds * 2));
				}
			}
		}

		public async Task<HNResult> GetTopStoriesAsync(
			int limit = 50,
			CancellationToken ct = default)
		{
			try
			{
				var ids = await RetryAsync(
					c => FetchItemWithThrottleAsync<List<int>>("topstories.json", -1, c),
					ct,
					maxAttempts: 3,
					initialDelay: TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);

				if (ids == null)
					return HNResult.OnFail("Failed to retrieve story IDs.");

				var take = Math.Min(limit, ids.Count);
				var idSlice = ids.Take(take).ToArray();

				var tasks = idSlice
					.Select(id => FetchItemWithThrottleAsync<Post>($"item/{id}.json", id, ct))
					.ToArray();

				var results = await Task.WhenAll(tasks).ConfigureAwait(false);
				var posts = results.Where(p => p != null).ToList()!;

				return HNResult.OnSuccess(posts!);
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				throw;
			}
			catch (HttpRequestException ex)
			{
				System.Diagnostics.Debug.WriteLine(
					$"GetTopStories network error: {ex.Message}; Status={(int?)ex.StatusCode}; Inner: {ex.InnerException?.Message}");
				return HNResult.OnNetworkFail("No internet connection or server unreachable.");
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"GetTopStories unexpected: {ex}");
				return HNResult.OnFail($"Unexpected error: {ex.Message}");
			}
		}

		public async Task<Post?> GetItemAsync(int id, CancellationToken ct = default)
		{
			return await FetchItemWithThrottleAsync<Post>($"item/{id}.json", id, ct)
				.ConfigureAwait(false);
		}

		/// <summary>
		/// Fetch the complete comment forest using bounded breadth-first network work.
		/// Only a small batch of comment fetch tasks exists at any one time.
		/// </summary>
		public async Task<List<Comment>> GetCommentsTreeAsync(
			Post post,
			CancellationToken ct = default)
		{
			return (await GetCommentsTreeResultAsync(post, ct).ConfigureAwait(false)).Comments;
		}

		private static List<Comment> BuildCommentRoots(
			Dictionary<int, CommentRaw> rawById,
			IEnumerable<int> rootIds)
		{
			var built = new Dictionary<int, List<Comment>>();
			var building = new HashSet<int>();

			List<Comment> BuildNodes(int id)
			{
				if (built.TryGetValue(id, out var cached))
					return cached;

				if (!rawById.TryGetValue(id, out var raw))
					return [];

				if (!building.Add(id))
					return [];

				var children = new List<Comment>();
				if (raw.Kids != null)
				{
					foreach (var childId in raw.Kids)
						children.AddRange(BuildNodes(childId));
				}

				building.Remove(id);

				var hasText = !string.IsNullOrWhiteSpace(raw.Text);
				var hasBy = !string.IsNullOrWhiteSpace(raw.By);
				var isDeleted = raw.Deleted == true;
				var isDead = raw.Dead == true;
				var isEmptyPlaceholder = !isDeleted && !isDead && !hasText && !hasBy;

				if (isEmptyPlaceholder)
				{
					built[id] = children;
					return children;
				}

				var node = new Comment
				{
					Id = raw.Id,
					By = raw.By,
					Text = raw.Text ?? string.Empty,
					Time = raw.Time,
					Children = children,
					Deleted = raw.Deleted ?? false,
					Dead = raw.Dead ?? false,
					Descendants = raw.Descendants ?? children.Count,
					Score = raw.Score
				};

				var result = new List<Comment> { node };
				built[id] = result;
				return result;
			}

			var roots = new List<Comment>();
			foreach (var rootId in rootIds)
				roots.AddRange(BuildNodes(rootId));

			return roots;
		}

		private async Task<CommentRaw?> FetchCommentRawSafeAsync(
			int id,
			CancellationToken ct)
		{
			try
			{
				return await FetchItemWithThrottleAsync<CommentRaw>($"item/{id}.json", id, ct)
					.ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine(
					$"Comment {id} fetch failed: {ex.Message}");
				return null;
			}
		}

		private async Task<T?> FetchItemWithThrottleAsync<T>(
			string path,
			int id,
			CancellationToken ct) where T : class
		{
			var cacheKey = $"{typeof(T).FullName}:{id}";

			if (_itemCache.TryGetValue(cacheKey, out var cached))
				return cached as T;

			var request = _itemInFlight.GetOrAdd(
				cacheKey,
				_ => new Lazy<Task<object?>>(
					() => FetchAndCacheAsync<T>(path, cacheKey),
					LazyThreadSafetyMode.ExecutionAndPublication));

			try
			{
				var value = await request.Value.WaitAsync(ct).ConfigureAwait(false);
				return value as T;
			}
			finally
			{
				if (request.IsValueCreated && request.Value.IsCompleted)
				{
					if (_itemInFlight.TryGetValue(cacheKey, out var current) &&
						ReferenceEquals(current, request))
					{
						_itemInFlight.TryRemove(cacheKey, out _);
					}
				}
			}
		}

		private async Task<object?> FetchAndCacheAsync<T>(string path, string cacheKey)
			where T : class
		{
			await _throttle.WaitAsync().ConfigureAwait(false);
			try
			{
				if (_itemCache.TryGetValue(cacheKey, out var cached))
					return cached;

				var obj = await GetAsync<T>(path, CancellationToken.None).ConfigureAwait(false);
				if (obj != null)
					_itemCache.TryAdd(cacheKey, obj);

				return obj;
			}
			finally
			{
				_throttle.Release();
			}
		}

		private static async Task<T?> GetAsync<T>(string path, CancellationToken ct)
		{
			using var resp = await http.GetAsync(
				path,
				HttpCompletionOption.ResponseHeadersRead,
				ct).ConfigureAwait(false);

			if (resp.StatusCode == HttpStatusCode.NotFound)
				return default;

			if (!resp.IsSuccessStatusCode)
			{
				throw new HttpRequestException(
					$"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} for {path}",
					inner: null,
					statusCode: resp.StatusCode);
			}

			await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
			return await JsonSerializer.DeserializeAsync<T>(stream, jsonOptions, ct)
				.ConfigureAwait(false);
		}

		public async Task<(string? Type, int Id, int? Parent)> GetItemInfoAsync(
			int id,
			CancellationToken ct = default)
		{
			ct.ThrowIfCancellationRequested();

			var raw = await FetchItemWithThrottleAsync<HnItemRaw>(
				$"item/{id}.json",
				id,
				ct).ConfigureAwait(false);

			if (raw == null)
				return (null, id, null);

			return (raw.Type, raw.Id, raw.Parent);
		}

		public async Task<(Post? Story, int? CommentId)> GetStoryContextAsync(
			int itemId,
			CancellationToken ct = default)
		{
			var currentId = itemId;

			while (true)
			{
				ct.ThrowIfCancellationRequested();

				var raw = await FetchItemWithThrottleAsync<HnItemRaw>(
					$"item/{currentId}.json",
					currentId,
					ct).ConfigureAwait(false);

				if (raw == null)
					return (null, null);

				if (string.Equals(raw.Type, "story", StringComparison.OrdinalIgnoreCase))
				{
					var story = await GetItemAsync(raw.Id, ct).ConfigureAwait(false);
					return (story, itemId == raw.Id ? null : itemId);
				}

				if (!string.Equals(raw.Type, "comment", StringComparison.OrdinalIgnoreCase) ||
					!raw.Parent.HasValue)
				{
					return (null, null);
				}

				currentId = raw.Parent.Value;
			}
		}

		private sealed class CommentRawFetchResult
		{
			public CommentRaw? Raw { get; init; }
			public bool IsNetworkError { get; init; }
		}

		public sealed class CommentTreeResult
		{
			public bool Success { get; init; }
			public bool IsNetworkError { get; init; }
			public List<Comment> Comments { get; init; } = [];
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

		private sealed class CommentRaw
		{
			public int Id { get; set; }
			public string? By { get; set; }
			public string? Text { get; set; }
			public long Time { get; set; }
			public List<int>? Kids { get; set; }
			public int? Descendants { get; set; }
			public int? Score { get; set; }
			public bool? Deleted { get; set; }
			public bool? Dead { get; set; }
		}

		/// <summary>
		/// Streams top-level comment branches one at a time. Each yielded comment
		/// contains its complete descendant tree, preserving the current TreeView model
		/// while allowing the UI to start rendering before the entire thread is fetched.
		/// </summary>
		public async IAsyncEnumerable<Comment> GetCommentsStreamAsync(
			Post post,
			[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
		{
			if (post?.Kids == null || post.Kids.Count == 0)
				yield break;

			foreach (var rootId in post.Kids)
			{
				ct.ThrowIfCancellationRequested();

				var roots = await GetCommentBranchAsync(rootId, ct).ConfigureAwait(false);
				foreach (var comment in roots)
				{
					ct.ThrowIfCancellationRequested();
					yield return comment;
				}
			}
		}

		private async Task<List<Comment>> GetCommentBranchAsync(
			int rootId,
			CancellationToken ct)
		{
			var rawById = new Dictionary<int, CommentRaw>();
			var pending = new Queue<int>();
			var seen = new HashSet<int>();

			pending.Enqueue(rootId);
			seen.Add(rootId);

			while (pending.Count > 0)
			{
				ct.ThrowIfCancellationRequested();

				var batch = new List<int>(Math.Min(CommentFetchBatchSize, pending.Count));
				while (pending.Count > 0 && batch.Count < CommentFetchBatchSize)
					batch.Add(pending.Dequeue());

				var tasks = batch.Select(id => FetchCommentRawSafeAsync(id, ct)).ToArray();
				var raws = await Task.WhenAll(tasks).ConfigureAwait(false);

				foreach (var raw in raws)
				{
					ct.ThrowIfCancellationRequested();
					if (raw == null)
						continue;

					rawById[raw.Id] = raw;

					if (raw.Kids == null)
						continue;

					foreach (var childId in raw.Kids)
					{
						if (seen.Add(childId))
							pending.Enqueue(childId);
					}
				}
			}

			return BuildCommentRoots(rawById, [rootId]);
		}

		public async Task<CommentTreeResult> GetCommentsTreeResultAsync(
	Post post,
	CancellationToken ct = default)
		{
			if (post?.Kids == null || post.Kids.Count == 0)
			{
				return new CommentTreeResult
				{
					Success = true,
					IsNetworkError = false,
					Comments = []
				};
			}

			var rawById = new Dictionary<int, CommentRaw>();
			var pending = new Queue<int>();
			var seen = new HashSet<int>();
			var networkError = false;

			foreach (var kidId in post.Kids)
			{
				if (seen.Add(kidId))
					pending.Enqueue(kidId);
			}

			while (pending.Count > 0)
			{
				ct.ThrowIfCancellationRequested();

				var batch = new List<int>(Math.Min(CommentFetchBatchSize, pending.Count));
				while (pending.Count > 0 && batch.Count < CommentFetchBatchSize)
					batch.Add(pending.Dequeue());

				var tasks = batch
					.Select(id => FetchCommentRawResultAsync(id, ct))
					.ToArray();

				var results = await Task.WhenAll(tasks).ConfigureAwait(false);

				foreach (var item in results)
				{
					ct.ThrowIfCancellationRequested();

					if (item.IsNetworkError)
						networkError = true;

					var raw = item.Raw;
					if (raw == null)
						continue;

					rawById[raw.Id] = raw;

					if (raw.Kids == null)
						continue;

					foreach (var childId in raw.Kids)
					{
						if (seen.Add(childId))
							pending.Enqueue(childId);
					}
				}
			}

			return new CommentTreeResult
			{
				Success = !networkError,
				IsNetworkError = networkError,
				Comments = BuildCommentRoots(rawById, post.Kids)
			};
		}

		private async Task<CommentRawFetchResult> FetchCommentRawResultAsync(
	int id,
	CancellationToken ct)
		{
			try
			{
				return new CommentRawFetchResult
				{
					Raw = await FetchItemWithThrottleAsync<CommentRaw>($"item/{id}.json", id, ct)
						.ConfigureAwait(false),
					IsNetworkError = false
				};
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine(
					$"Comment {id} fetch failed: {ex.Message}");

				return new CommentRawFetchResult
				{
					Raw = null,
					IsNetworkError = true
				};
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
		public List<Comment> Children { get; set; } = [];
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
					return $"+{Score.Value} · {TimeAgo}";

				return TimeAgo;
			}
		}
	}

	public class HNResult
	{
		public bool Success { get; private set; }
		public bool IsNetworkError { get; private set; }
		public string? ErrorMessage { get; private set; }
		public List<Post> Posts { get; private set; } = [];

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
		public string? Text { get; set; }
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
