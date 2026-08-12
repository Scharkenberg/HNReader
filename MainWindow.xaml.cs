using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Display;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using Windows.Networking.Connectivity;
using System.Text.RegularExpressions;
using Windows.System;

namespace HNReader
{
	public sealed partial class MainWindow : Window
	{
		private readonly HNClient client = new();
		private bool _isRefreshing = false;
		public ObservableCollection<Comment> Comments { get; } = new();
		private AppWindow _appWindow;
		private CancellationTokenSource? _resizeCts;
		private CancellationTokenSource? _chevronCts;
		private CancellationTokenSource? _currentCommentsLoadCts;
		private bool _isClosing = false;

		// Posts incremental state
		private ObservableCollection<Post> _posts = new ObservableCollection<Post>();
		private CancellationTokenSource? _postsLoadCts;
		private readonly SemaphoreSlim _postsLoadLock = new SemaphoreSlim(1, 1);
		private bool _postsHasMore = true;
		private int _postsOffset = 0;
		private const int PostsBatchSize = 5;
		private bool _isFirstLoad = false;
		private int _minInitialPosts = 40;
		private bool _isPostsLoading = false;
		private const double LoadThresholdPx = 100.0;
		private ScrollViewer? _postsScrollViewer;
		// ephemeral post shown only in the content pane (not part of _posts, not cached)
		private Post? _ephemeralPost;
		private int? _ephemeralCommentId;

		private readonly SemaphoreSlim _saveCacheLock = new SemaphoreSlim(1, 1);
		private const int SaveCacheMaxRetries = 6;
		private static readonly TimeSpan SaveCacheInitialDelay = TimeSpan.FromMilliseconds(150);
		private const string PostsCacheTempFileNamePrefix = "posts_cache.tmp";

		private static readonly TimeSpan FetchInterval = TimeSpan.FromHours(6);
		private const string LastFetchKey = "LastFetchUtc";
		private const string PostsCacheFileName = "posts_cache.json";
		private const string PostsCacheTempFileName = "posts_cache.tmp.json";
		private const int PostsCacheVersion = 1; // bump if DTO changes
		private readonly Dictionary<int, List<CommentCacheItem>> _cachedComments = new();
		private CancellationTokenSource? _saveOfflineCts;
		private sealed class CommentCacheItem
		{
			public int Id { get; set; }
			public int? ParentId { get; set; } // null for top-level
			public string? By { get; set; }
			public string? Text { get; set; }
			public long Time { get; set; }
			public List<int>? ChildIds { get; set; } // optional, helps reconstruct tree
		}

		private sealed class PostCacheItem
		{
			public int Id { get; set; }
			public string? Title { get; set; }
			public string? By { get; set; }
			public int Score { get; set; }
			public long Time { get; set; }
			public string? Url { get; set; }
			public string? Text { get; set; }
			public int Descendants { get; set; } // NEW: total number of comments
			public List<CommentCacheItem>? Comments { get; set; }
		}

		private sealed class PostsCache
		{
			public int Version { get; set; } = PostsCacheVersion;
			public List<PostCacheItem> Items { get; set; } = new();
		}

		private const double FontStep = 1;
		private const double MinFontDelta = -4.0;
		private const double MaxFontDelta = 6.0;
		private const double IndentStepPerFontPoint = 2.0;
		private const double MinIndentDelta = -8.0;
		private const double MaxIndentDelta = 12.0;

		private Windows.Graphics.Display.DisplayInformation? _displayInfo;
		private double _cachedScale = 1.0;
		private Microsoft.UI.Windowing.AppWindow? _cachedAppWindow;

		private FrameworkElement RootElement => (FrameworkElement)this.Content;

		public MainWindow()
		{
			this.InitializeComponent();
			HtmlRendererHelpers.LinkClickHandler = async (url) => await HandleLinkClickAsync(url).ConfigureAwait(false);

			var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
			var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
			_appWindow = AppWindow.GetFromWindowId(windowId);

			ApplySavedFontAndIndent();
#pragma warning disable CS4014
			_ = CleanupLeftoverTempFilesAsync();
#pragma warning restore CS4014

			var titleBar = _appWindow.TitleBar;
			titleBar.ButtonBackgroundColor = null;
			titleBar.ButtonForegroundColor = null;
			titleBar.ButtonHoverBackgroundColor = null;
			titleBar.ButtonHoverForegroundColor = null;
			titleBar.ButtonPressedBackgroundColor = null;
			titleBar.ButtonPressedForegroundColor = null;
			titleBar.ButtonInactiveBackgroundColor = null;
			titleBar.ButtonInactiveForegroundColor = null;

			var uiSettings = new Windows.UI.ViewManagement.UISettings();
			uiSettings.ColorValuesChanged += (s, e) =>
			{
				_ = DispatcherQueue.TryEnqueue(() =>
				{
					titleBar.ButtonBackgroundColor = null;
					titleBar.ButtonForegroundColor = null;
					titleBar.ButtonHoverBackgroundColor = null;
					titleBar.ButtonHoverForegroundColor = null;
					titleBar.ButtonPressedBackgroundColor = null;
					titleBar.ButtonPressedForegroundColor = null;
					titleBar.ButtonInactiveBackgroundColor = null;
					titleBar.ButtonInactiveForegroundColor = null;
				});
			};

			this.SizeChanged += MainWindow_SizeChanged;
			this.Closed += MainWindow_Closed;
			this.Activated += MainWindow_Activated;

			LeftPane.MinWidth = 360;
			LeftPane.MaxWidth = 480;
		}

		private void MainWindow_Activated(object? sender, Microsoft.UI.Xaml.WindowActivatedEventArgs e)
		{
			this.Activated -= MainWindow_Activated;

			_ = DispatcherQueue.TryEnqueue(() =>
			{
				try
				{
					_displayInfo = Windows.Graphics.Display.DisplayInformation.GetForCurrentView();
					_cachedScale = _displayInfo?.RawPixelsPerViewPixel ?? 1.0;
					if (_displayInfo != null) _displayInfo.DpiChanged += DisplayInfo_DpiChanged;

					var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
					var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
					_cachedAppWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

					EnsureAppWindowPreferredMinSize(800, 400);
				}
				catch
				{
					if (this.Content is FrameworkElement root)
					{
						root.MinWidth = 800;
						root.MinHeight = 400;
					}
				}
			});
		}

		private void DisplayInfo_DpiChanged(Windows.Graphics.Display.DisplayInformation sender, object args)
		{
			_ = DispatcherQueue.TryEnqueue(() =>
			{
				_cachedScale = sender.RawPixelsPerViewPixel;
				EnsureAppWindowPreferredMinSize(800, 400);
			});
		}

		private void MainWindow_SizeChanged(object sender, Microsoft.UI.Xaml.WindowSizeChangedEventArgs e)
		{
			const double minWidth = 800;
			const double minHeight = 400;

			var newWidth = e.Size.Width;
			var newHeight = e.Size.Height;

			if (newWidth < minWidth || newHeight < minHeight)
			{
				_resizeCts?.Cancel();
				_resizeCts = new CancellationTokenSource();
				_ = Task.Run(async () =>
				{
					try
					{
						await Task.Delay(180, _resizeCts.Token).ConfigureAwait(false);
						DispatcherQueue.TryEnqueue(() => { /* expensive UI update if needed */ });
					}
					catch (OperationCanceledException) { }
				});
			}
			EnsureAppWindowPreferredMinSize();
		}

		private void MainWindow_Closed(object? sender, WindowEventArgs e)
		{
			_isClosing = true;
			try
			{
				CancelPostsLoad();

				try { this.SizeChanged -= MainWindow_SizeChanged; } catch { }
				try { CommentsTree.Loaded -= CommentsTree_Loaded; } catch { }
				try { CommentsTree.LayoutUpdated -= CommentsTree_LayoutUpdated; } catch { }

				SafeCancelDispose(ref _currentCommentsLoadCts);
				SafeCancelDispose(ref _resizeCts);
				SafeCancelDispose(ref _chevronCts);

				if (_displayInfo != null)
				{
					try { _displayInfo.DpiChanged -= DisplayInfo_DpiChanged; } catch { }
					_displayInfo = null;
				}
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"MainWindow_Closed cleanup error: {ex}");
			}
		}

		private void EnsureAppWindowPreferredMinSize(int minWidthDip = 800, int minHeightDip = 400)
		{
			try
			{
				var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
				var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
				var appWindow = _cachedAppWindow ?? AppWindow.GetFromWindowId(windowId);
				_cachedAppWindow ??= appWindow;
				if (appWindow == null) return;

				var displayInfo = DisplayInformation.GetForCurrentView();
				var scale = displayInfo.RawPixelsPerViewPixel;
				var minWidthPx = (int)Math.Round(minWidthDip * scale);
				var minHeightPx = (int)Math.Round(minHeightDip * scale);

				var presenter = appWindow.Presenter;
				if (presenter != null)
				{
					if (presenter is OverlappedPresenter overlapped)
					{
						overlapped.PreferredMinimumHeight = minHeightPx;
						overlapped.PreferredMinimumWidth = minWidthPx;
						return;
					}
					else return;
				}

				this.MinWidth = minWidthDip;
				this.MinHeight = minHeightDip;
			}
			catch
			{
				try
				{
					this.MinWidth = minWidthDip;
					this.MinHeight = minHeightDip;
				}
				catch { }
			}
		}

		private void SetFontButtonsEnabled(bool enabled)
		{
			try
			{
				OpenInBrowserIcon.IsEnabled = enabled;
				IncreaseFontSizeIcon.IsEnabled = enabled;
				DecreaseFontSizeIcon.IsEnabled = enabled;
				ResetFontSizeIcon.IsEnabled = enabled;
			}
			catch { }
		}

		private void ApplySavedFontAndIndent()
		{
			var savedFont = LoadSetting("FontDelta", 0.0);
			var savedIndent = LoadSetting("IndentDelta", savedFont * IndentStepPerFontPoint);

			FontSizeAdjust.SetGlobalDelta(RootElement, savedFont);
			IndentAdjust.SetGlobalIndentDelta(RootElement, savedIndent);
		}

		private static void SafeCancelDispose(ref CancellationTokenSource? cts)
		{
			var old = Interlocked.Exchange(ref cts, null);
			if (old == null) return;
			try { old.Cancel(); } catch { }
			try { old.Dispose(); } catch { }
		}

		private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
		{
			await StartPostsInitialLoadAsync();
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
					DependencyObject? child = null;
					try { child = VisualTreeHelper.GetChild(cur, i); }
					catch { continue; }
					if (child != null) q.Enqueue(child);
				}
			}

			return null;
		}

		private static void SaveSetting(string key, double value)
		{
			try
			{
				var local = Windows.Storage.ApplicationData.Current.LocalSettings;
				local.Values[key] = value;
			}
			catch { }
		}

		private double LoadSetting(string key, double defaultValue)
		{
			try
			{
				var local = Windows.Storage.ApplicationData.Current.LocalSettings;
				if (local.Values.TryGetValue(key, out var o) && o is double d) return d;
				if (local.Values.TryGetValue(key, out var o2) && o2 is float f) return (double)f;
				if (local.Values.TryGetValue(key, out var o3) && o3 is int i) return (double)i;
			}
			catch { }
			return defaultValue;
		}

		private void SaveLastFetchTime(DateTimeOffset when)
		{
			// store as Unix seconds (SaveSetting already exists in your file)
			try { SaveSetting(LastFetchKey, (double)when.ToUnixTimeSeconds()); }
			catch { /* ignore */ }
		}

		private DateTimeOffset LoadLastFetchTime()
		{
			try
			{
				var v = LoadSetting(LastFetchKey, double.NaN);
				if (double.IsNaN(v)) return DateTimeOffset.MinValue;
				return DateTimeOffset.FromUnixTimeSeconds((long)v);
			}
			catch { return DateTimeOffset.MinValue; }
		}

		private static readonly JsonSerializerOptions _cacheJsonOptions = new JsonSerializerOptions
		{
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			WriteIndented = false
		};

		private async Task<bool> IsHackerNewsReachableAsync(CancellationToken ct)
		{
			try
			{
				var profile = NetworkInformation.GetInternetConnectionProfile();
				if (profile == null) return false;
				var level = profile.GetNetworkConnectivityLevel();
				if (level == NetworkConnectivityLevel.None || level == NetworkConnectivityLevel.LocalAccess) return false;
			}
			catch { /* fall back to HTTP probe */ }

			try
			{
				using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
				using var req = new HttpRequestMessage(HttpMethod.Head, "https://news.ycombinator.com/");
				using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
				return resp.StatusCode == HttpStatusCode.OK || resp.StatusCode == HttpStatusCode.Found;
			}
			catch { return false; }
		}
		private async Task CleanupLeftoverTempFilesAsync()
		{
			try
			{
				var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
				var items = await folder.GetItemsAsync().AsTask().ConfigureAwait(false);
				foreach (var item in items)
				{
					if (item.IsOfType(Windows.Storage.StorageItemTypes.File) &&
						item.Name.StartsWith(PostsCacheTempFileNamePrefix, StringComparison.OrdinalIgnoreCase))
					{
						try { await ((Windows.Storage.StorageFile)item).DeleteAsync().AsTask().ConfigureAwait(false); }
						catch { /* ignore */ }
					}
				}
			}
			catch { /* ignore */ }
		}

		private async Task SavePostsCacheAsync()
		{
			await _saveCacheLock.WaitAsync().ConfigureAwait(false);
			try
			{
				// Build DTO list
				var items = _posts.Select(p =>
				{
					_cachedComments.TryGetValue((int)p.Id, out var commentsForPost);
					return new PostCacheItem
					{
						Id = (int)p.Id,
						Title = p.Title,
						By = p.By,
						Score = p.Score,
						Time = p.Time,
						Url = p.Url,
						Text = p.Text,
						Descendants = p.Descendants,
						Comments = commentsForPost
					};
				}).ToList();

				var cache = new PostsCache { Items = items };

				var folder = Windows.Storage.ApplicationData.Current.LocalFolder;

				// Best-effort cleanup of stale temp files before creating a new one
				try { await CleanupLeftoverTempFilesAsync().ConfigureAwait(false); } catch { /* ignore */ }

				// Unique temp filename
				var uniqueTempName = $"{PostsCacheTempFileNamePrefix}.{Guid.NewGuid():N}.tmp";
				Windows.Storage.StorageFile? tempFile = null;

				try
				{
					tempFile = await folder.CreateFileAsync(uniqueTempName, Windows.Storage.CreationCollisionOption.ReplaceExisting).AsTask().ConfigureAwait(false);

					// Write compressed JSON to the temp file and ensure streams are closed
					using (var outStream = await tempFile.OpenStreamForWriteAsync().ConfigureAwait(false))
					{
						using var brotli = new BrotliStream(outStream, CompressionLevel.Optimal, leaveOpen: true);
						await JsonSerializer.SerializeAsync(brotli, cache, _cacheJsonOptions).ConfigureAwait(false);
						await brotli.FlushAsync().ConfigureAwait(false);
						await outStream.FlushAsync().ConfigureAwait(false);
					}
				}
				catch (Exception writeEx)
				{
					// If writing fails, try to delete temp file and rethrow
					try { if (tempFile != null) await tempFile.DeleteAsync().AsTask().ConfigureAwait(false); } catch { }
					System.Diagnostics.Debug.WriteLine($"SavePostsCacheAsync: write failed: {writeEx}");
					throw;
				}

				// Attempt to move temp -> final with retries on sharing/permission errors
				var finalFileName = PostsCacheFileName;
				int attempt = 0;
				var delay = SaveCacheInitialDelay;
				var rnd = new Random();

				while (true)
				{
					try
					{
						// Move temp -> final (ReplaceExisting)
						await tempFile.MoveAsync(folder, finalFileName, Windows.Storage.NameCollisionOption.ReplaceExisting).AsTask().ConfigureAwait(false);

						// Optionally log compressed size
						try
						{
							var finalFile = await folder.GetFileAsync(finalFileName).AsTask().ConfigureAwait(false);
							var props = await finalFile.GetBasicPropertiesAsync().AsTask().ConfigureAwait(false);
							System.Diagnostics.Debug.WriteLine($"Saved posts+comments cache (brotli) {props.Size} bytes");
						}
						catch { }

						break; // success
					}
					catch (System.Runtime.InteropServices.COMException comEx) when ((uint)comEx.HResult == 0x80070020u /* ERROR_SHARING_VIOLATION */)
					{
						attempt++;
						System.Diagnostics.Debug.WriteLine($"SavePostsCacheAsync: sharing violation on attempt {attempt}: {comEx.Message}");

						if (attempt >= SaveCacheMaxRetries)
						{
							System.Diagnostics.Debug.WriteLine($"SavePostsCacheAsync: giving up after {attempt} attempts (sharing violation).");
							try { await tempFile.DeleteAsync().AsTask().ConfigureAwait(false); } catch { }
							break;
						}

						// backoff + jitter
						var jitter = TimeSpan.FromMilliseconds(rnd.Next(0, 120));
						await Task.Delay(delay + jitter).ConfigureAwait(false);
						delay = TimeSpan.FromMilliseconds(Math.Min(2000, delay.TotalMilliseconds * 2));
						continue;
					}
					catch (UnauthorizedAccessException uaEx)
					{
						attempt++;
						System.Diagnostics.Debug.WriteLine($"SavePostsCacheAsync: UnauthorizedAccessException on attempt {attempt}: {uaEx.Message}");

						if (attempt >= SaveCacheMaxRetries)
						{
							System.Diagnostics.Debug.WriteLine($"SavePostsCacheAsync: giving up after {attempt} attempts (unauthorized).");
							try { await tempFile.DeleteAsync().AsTask().ConfigureAwait(false); } catch { }
							break;
						}

						var jitter = TimeSpan.FromMilliseconds(rnd.Next(0, 120));
						await Task.Delay(delay + jitter).ConfigureAwait(false);
						delay = TimeSpan.FromMilliseconds(Math.Min(2000, delay.TotalMilliseconds * 2));
						continue;
					}
					catch (System.IO.FileNotFoundException fnf)
					{
						// Temp file disappeared; nothing to do
						System.Diagnostics.Debug.WriteLine($"SavePostsCacheAsync: temp file not found during move: {fnf.Message}");
						break;
					}
					catch (Exception ex)
					{
						System.Diagnostics.Debug.WriteLine($"SavePostsCacheAsync (compressed) failed: {ex}");
						try { await tempFile.DeleteAsync().AsTask().ConfigureAwait(false); } catch { }
						break;
					}
				}
			}
			finally
			{
				_saveCacheLock.Release();
			}
		}

		private async Task<bool> LoadPostsCacheAsync()
		{
			try
			{
				var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
				var file = await folder.TryGetItemAsync(PostsCacheFileName) as Windows.Storage.StorageFile;
				if (file == null) return false;

				using var inStream = await file.OpenStreamForReadAsync();
				using var brotli = new BrotliStream(inStream, CompressionMode.Decompress, leaveOpen: true);
				var cache = await JsonSerializer.DeserializeAsync<PostsCache>(brotli, _cacheJsonOptions).ConfigureAwait(false);
				if (cache == null || cache.Items == null || cache.Items.Count == 0) return false;
				if (cache.Version != PostsCacheVersion) return false;

				// populate in-memory caches and UI
				_cachedComments.Clear();
				await RunOnUiAsync(() =>
				{
					_posts.Clear();
					foreach (var it in cache.Items)
					{
						_posts.Add(new Post
						{
							Id = it.Id,
							Title = it.Title,
							By = it.By,
							Score = it.Score,
							Time = it.Time,
							Url = it.Url,
							Descendants = it.Descendants,
							Text = it.Text
						});

						if (it.Comments != null && it.Comments.Count > 0)
						{
							_cachedComments[it.Id] = it.Comments;
						}
					}
					ApplySavedFontAndIndent();
				});

				_postsOffset = _posts.Count;
				_postsHasMore = true;
				return true;
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"LoadPostsCacheAsync (compressed) failed: {ex}");
				return false;
			}
		}

		private async Task LoadUntilMinAsync(CancellationToken ct)
		{
			// serialize with the same semaphore so we don't conflict with other loads
			await _postsLoadLock.WaitAsync();
			try
			{
				_isPostsLoading = true;

				// show spinner
				try
				{
					await RunOnUiAsync(() =>
					{
						if (PostsFooterProgress != null)
						{
							PostsFooterProgress.IsActive = true;
							PostsFooterProgress.Visibility = Visibility.Visible;
						}
					});
				}
				catch { }

				// Keep fetching until we have enough posts or there are no more
				while (!_isClosing && !ct.IsCancellationRequested && _postsHasMore && _posts.Count < _minInitialPosts)
				{
					// Ask the API for enough IDs to cover the minimum target on first load.
					// Also ensure we request at least one batch beyond current offset.
					var requestCount = Math.Max(_postsOffset + PostsBatchSize, _minInitialPosts);

					var result = await client.GetTopStoriesAsync(requestCount);
					System.Diagnostics.Debug.WriteLine($"LoadUntilMinAsync: GetTopStoriesAsync returned Success={result.Success} Posts.Count={result.Posts?.Count ?? 0}");

					if (ct.IsCancellationRequested) break;

					if (!result.Success)
					{
						await RunOnUiAsync(async () => await ShowErrorDialog(result.ErrorMessage ?? "Unknown error."));
						break;
					}

					// take the next batch slice
					if (result.Posts == null || result.Posts.Count == 0)
					{
						System.Diagnostics.Debug.WriteLine("Load: result.Posts is null or empty; aborting slice.");
						break;
					}
					var slice = result.Posts.Skip(_postsOffset).Take(PostsBatchSize).ToList();
					System.Diagnostics.Debug.WriteLine($"LoadUntilMinAsync: slice.Count={slice.Count} _postsOffset={_postsOffset}");

					await RunOnUiAsync(() =>
					{
						foreach (var p in slice)
							_posts.Add(p);

						ApplySavedFontAndIndent();
					});

					_postsOffset += slice.Count;
					_postsHasMore = slice.Count >= PostsBatchSize && result.Posts.Count >= _postsOffset;
					SaveLastFetchTime(DateTimeOffset.UtcNow);
#pragma warning disable CS4014
					await SavePostsCacheAsync().ConfigureAwait(false);
#pragma warning restore CS4014

					System.Diagnostics.Debug.WriteLine($"LoadUntilMinAsync: appended; _posts.Count={_posts.Count}; _postsHasMore={_postsHasMore}");

					// If we still need more and there are more, loop again.
					if (_posts.Count < _minInitialPosts && _postsHasMore)
					{
						// small delay so UI can render and avoid tight loop
						await Task.Delay(50, ct).ConfigureAwait(false);
						continue;
					}

					break;
				}
			}
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"LoadUntilMinAsync error: {ex}");
			}
			finally
			{
				_isPostsLoading = false;
				try
				{
					await RunOnUiAsync(() =>
					{
						if (PostsFooterProgress != null)
						{
							PostsFooterProgress.IsActive = false;
							PostsFooterProgress.Visibility = _postsHasMore ? Visibility.Visible : Visibility.Collapsed;
						}
					});
				}
				catch { }
				_postsLoadLock.Release();
			}
		}

		private async Task FillInitialPostsAsync(CancellationToken ct)
		{
			await _postsLoadLock.WaitAsync();
			try
			{
				_isPostsLoading = true;

				// show spinner on UI thread
				try
				{
					await RunOnUiAsync(() =>
					{
						if (PostsFooterProgress != null)
						{
							PostsFooterProgress.IsActive = true;
							PostsFooterProgress.Visibility = Visibility.Visible;
						}
					});
				}
				catch { }

				// initial request: ask for at least the minimum target
				var requestCount = Math.Max(_minInitialPosts, PostsBatchSize);

				HNResult result = await client.GetTopStoriesAsync(requestCount).ConfigureAwait(false);

				if (!result.Success)
				{
					await RunOnUiAsync(async () => await ShowErrorDialog(result.ErrorMessage ?? "Unknown error."));
					return;
				}

				// keep appending batches until we reach the minimum or run out
				while (!ct.IsCancellationRequested && _posts.Count < _minInitialPosts)
				{
					// take next UI-sized slice from the already-fetched IDs
					var slice = result.Posts.Skip(_postsOffset).Take(PostsBatchSize).ToList();
					if (slice.Count == 0) break;

					// marshal the append to UI thread
					await RunOnUiAsync(() =>
					{
						foreach (var p in slice) _posts.Add(p);
						ApplySavedFontAndIndent();
					});

					_postsOffset += slice.Count;
					_postsHasMore = slice.Count >= PostsBatchSize && result.Posts.Count >= _postsOffset;
					SaveLastFetchTime(DateTimeOffset.UtcNow);
#pragma warning disable CS4014
					await SavePostsCacheAsync().ConfigureAwait(false);
#pragma warning restore CS4014

					// if we still need more but the initial result didn't include enough IDs, request more IDs
					if (_posts.Count < _minInitialPosts && _postsHasMore)
					{
						var needed = Math.Max(_minInitialPosts, _postsOffset + PostsBatchSize);
						result = await client.GetTopStoriesAsync(needed).ConfigureAwait(false);
						if (!result.Success) break;
						// small pause so UI can render
						await Task.Delay(50, ct).ConfigureAwait(false);
						continue;
					}

					break;
				}
			}
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"FillInitialPostsAsync error: {ex}");
			}
			finally
			{
				_isPostsLoading = false;
				try
				{
					await RunOnUiAsync(() =>
					{
						if (PostsFooterProgress != null)
						{
							PostsFooterProgress.IsActive = false;
							PostsFooterProgress.Visibility = _postsHasMore ? Visibility.Visible : Visibility.Collapsed;
						}
					});
				}
				catch { }
				_postsLoadLock.Release();
			}
		}

		private async Task StartPostsInitialLoadAsync(bool force = false)
		{
			await RunOnUiAsync(() =>
			{
				PostsList.ItemsSource = _posts;
			});

			_postsOffset = 0;
			_postsHasMore = true;
			await RunOnUiAsync(() => _posts.Clear());

			_postsLoadCts?.Cancel();
			_postsLoadCts?.Dispose();
			_postsLoadCts = new CancellationTokenSource();

			// If not forced, decide whether to fetch or load cache
			if (!force)
			{
				var last = LoadLastFetchTime();

				// Probe site reachability on background thread with cancellation token
				var ct = _postsLoadCts?.Token ?? CancellationToken.None;
				bool reachable = false;
				try
				{
					reachable = await IsHackerNewsReachableAsync(ct).ConfigureAwait(false);

				}
				catch { reachable = false; }

				if (reachable)
				{
					// Site reachable: respect the 6-hour rule
					if (last != DateTimeOffset.MinValue && (DateTimeOffset.UtcNow - last) < FetchInterval)
					{
						System.Diagnostics.Debug.WriteLine($"Site reachable but last fetch was {(DateTimeOffset.UtcNow - last)} ago — skipping network fetch and using cache if available.");
						var loaded = await LoadPostsCacheAsync().ConfigureAwait(false);
						if (loaded)
						{
							System.Diagnostics.Debug.WriteLine($"Loaded {_posts.Count} posts from cache (recent).");
							return;
						}
						// no cache -> fall through to network fetch
					}
					// else: last fetch too old or missing -> proceed to network fetch
				}
				else
				{
					// Site not reachable: prefer cache
					System.Diagnostics.Debug.WriteLine("Hacker News not reachable (network/site probe failed). Attempting to load cache.");
					var loaded = await LoadPostsCacheAsync().ConfigureAwait(false);
					if (loaded)
					{
						System.Diagnostics.Debug.WriteLine($"Loaded {_posts.Count} posts from cache (offline).");
						return;
					}
					// If cache missing, fall through and attempt network fetch (optional)
					System.Diagnostics.Debug.WriteLine("No usable cache found; will attempt network fetch despite probe failure.");
				}
			}

			_isFirstLoad = true;

			try
			{
				await FillInitialPostsAsync(_postsLoadCts!.Token);
			}
			finally
			{
				_isFirstLoad = false;
			}
		}

		/// <summary>
		/// Returns the numeric item id if the URL is a Hacker News item link, otherwise null.
		/// </summary>
		private static int? ParseHackerNewsItemId(string? url)
		{
			if (string.IsNullOrWhiteSpace(url))
				return null;

			var value = System.Net.WebUtility.HtmlDecode(url).Trim();

			// A fragment (#...) does not change which HN item the URL identifies.
			// Remove it before parsing the query string.
			var fragmentIndex = value.IndexOf('#');
			if (fragmentIndex >= 0)
				value = value[..fragmentIndex];

			// Absolute HN URL:
			// https://news.ycombinator.com/item?id=12345
			// http://news.ycombinator.com/item?id=12345&foo=bar
			if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
			{
				if (!string.Equals(
						absolute.Host,
						"news.ycombinator.com",
						StringComparison.OrdinalIgnoreCase))
				{
					return null;
				}

				if (!string.Equals(
						absolute.AbsolutePath,
						"/item",
						StringComparison.OrdinalIgnoreCase))
				{
					return null;
				}

				var match = Regex.Match(
					absolute.Query,
					@"(?:\?|&)id=(\d+)(?:&|$)",
					RegexOptions.IgnoreCase);

				if (match.Success &&
					int.TryParse(match.Groups[1].Value, out var absoluteId))
				{
					return absoluteId;
				}

				return null;
			}

			// Relative HN URL:
			// item?id=12345
			// /item?id=12345
			if (value.StartsWith("/", StringComparison.Ordinal))
				value = value[1..];

			if (!value.StartsWith(
					"item?",
					StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}

			var relativeMatch = Regex.Match(
				value,
				@"^item\?(?:.*&)?id=(\d+)(?:&.*)?$",
				RegexOptions.IgnoreCase);

			if (relativeMatch.Success &&
				int.TryParse(relativeMatch.Groups[1].Value, out var relativeId))
			{
				return relativeId;
			}

			return null;
		}
		/// <summary>
		/// Handle a clicked link. If it's a Hacker News item link, load that post and its comments into the content pane.
		/// Otherwise fall back to the default behavior (open externally).
		/// </summary>
		private async Task HandleLinkClickAsync(string url)
		{
			try
			{
				var id = ParseHackerNewsItemId(url);

				if (!id.HasValue)
				{
					if (Uri.TryCreate(url, UriKind.Absolute, out var externalUri))
						await Windows.System.Launcher.LaunchUriAsync(externalUri);

					return;
				}

				_currentCommentsLoadCts?.Cancel();
				_currentCommentsLoadCts?.Dispose();
				_currentCommentsLoadCts = new CancellationTokenSource();

				var ct = _currentCommentsLoadCts.Token;

				await RunOnUiAsync(() =>
				{
					_ephemeralPost = null;
					_ephemeralCommentId = null;

					Comments.Clear();
					CommentsTree.RootNodes.Clear();
				});

				var info = await client.GetItemInfoAsync(id.Value, ct).ConfigureAwait(false);

				if (string.Equals(info.Type, "story", StringComparison.OrdinalIgnoreCase))
				{
					var post = await client.GetItemAsync(id.Value)
						.ConfigureAwait(false);

					if (post == null)
					{
						await RunOnUiAsync(async () =>
							await ShowErrorDialog(
								$"Failed to load HN item {id.Value}."));

						return;
					}

					_ephemeralPost = post;
					_ephemeralCommentId = null;

					await LoadPostContentAsync(
						post,
						isEphemeral: true,
						focusCommentId: null,
						ct);

					return;
				}

				if (string.Equals(info.Type, "comment", StringComparison.OrdinalIgnoreCase))
				{
					var (story, commentId) =
						await client.GetStoryContextAsync(id.Value, ct)
							.ConfigureAwait(false);

					if (story == null)
					{
						await RunOnUiAsync(async () =>
							await ShowErrorDialog(
								$"Unable to locate the story containing comment {id.Value}."));

						return;
					}

					_ephemeralPost = story;
					_ephemeralCommentId = commentId;

					await LoadPostContentAsync(
						story,
						isEphemeral: true,
						focusCommentId: commentId,
						ct);

					return;
				}

				await RunOnUiAsync(async () => await ShowErrorDialog($"HN item {id.Value} is not a readable story or comment."));
			}
			catch (OperationCanceledException)
			{
				// Expected when another link is clicked.
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine(
					$"HandleLinkClickAsync error: {ex}");
			}
		}
		private async void RefreshButton_Click(object sender, RoutedEventArgs e)
		{
			if (_isRefreshing) return;

			_isRefreshing = true;
			RefreshButton.IsEnabled = false;

			try
			{
				// 1) Force fetch posts and metadata (existing behavior)
				await StartPostsInitialLoadAsync(force: true);

				// 2) If there is a currently selected/visible post, force-refresh its comments too
				//    Determine the "current" post in whatever way your UI uses; common options:
				//    - PostsList.SelectedItem
				//    - the post currently displayed in the post-details pane
				Post? current = null;
				try
				{
					current = PostsList.SelectedItem as Post;
				}
				catch { current = null; }

				if (current != null)
				{
					_currentCommentsLoadCts?.Cancel();
					_currentCommentsLoadCts?.Dispose();
					_currentCommentsLoadCts = new CancellationTokenSource();

					var ct = _currentCommentsLoadCts.Token;

					try
					{
						await LoadPostContentAsync(
							current,
							isEphemeral: false,
							focusCommentId: null,
							ct,
							forceNetwork: true);
					}
					catch (OperationCanceledException)
					{
						// Expected if another load supersedes this one.
					}
					catch (Exception ex)
					{
						System.Diagnostics.Debug.WriteLine(
							$"Refresh: failed to refresh comments for post {current.Id}: {ex}");
					}
				}

				// scroll to top of posts list as before
				if (PostsList.Items?.Count > 0)
				{
					PostsList.ScrollIntoView(PostsList.Items[0]);
				}
			}
			finally
			{
				RefreshButton.IsEnabled = true;
				_isRefreshing = false;
			}
		}

		// Dispatcher helpers
		/// <summary>
		/// Run an action on the UI thread and await completion. Returns a Task so callers can await UI updates.
		/// Uses DispatcherQueue.TryEnqueue to avoid COM/threading issues with WinUI controls.
		/// </summary>
		private Task RunOnUiAsync(Action action)
		{
			if (action == null)
				return Task.CompletedTask;

			var dispatcher = this.DispatcherQueue;

			if (dispatcher == null)
			{
				return Task.FromException(
					new InvalidOperationException("MainWindow DispatcherQueue is unavailable."));
			}

			var tcs = new TaskCompletionSource<object?>(
				TaskCreationOptions.RunContinuationsAsynchronously);

			if (!dispatcher.TryEnqueue(() =>
			{
				try
				{
					action();
					tcs.TrySetResult(null);
				}
				catch (Exception ex)
				{
					tcs.TrySetException(ex);
				}
			}))
			{
				tcs.TrySetException(
					new InvalidOperationException(
						"DispatcherQueue.TryEnqueue returned false."));
			}

			return tcs.Task;
		}
		private Task RunOnUiAsyncFunc(Func<Task> func)
		{
			var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
			var dq = this.DispatcherQueue;
			if (dq == null)
			{
				_ = Task.Run(async () =>
				{
					try { await func(); tcs.SetResult(null); }
					catch (Exception ex) { tcs.SetException(ex); }
				});
				return tcs.Task;
			}

			var enqueued = dq.TryEnqueue(async () =>
			{
				try { await func(); tcs.SetResult(null); }
				catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"RunOnUiAsyncFunc: func threw: {ex}"); tcs.SetException(ex); }
			});

			if (!enqueued)
			{
				var ex = new InvalidOperationException("DispatcherQueue.TryEnqueue returned false for RunOnUiAsyncFunc");
				System.Diagnostics.Debug.WriteLine($"RunOnUiAsyncFunc: TryEnqueue returned false");
				tcs.SetException(ex);
			}

			return tcs.Task;
		}

		private TreeViewNode? FindNodeByCommentIdRecursive(TreeViewNode node, string id)
		{
			if (node.Content is Comment c && c.Id.ToString() == id) return node;
			foreach (var child in node.Children)
			{
				var f = FindNodeByCommentIdRecursive(child, id);
				if (f != null) return f;
			}
			return null;
		}

		private void FocusCommentInTree(int commentId)
		{
			var target = default(TreeViewNode);

			foreach (var root in CommentsTree.RootNodes)
			{
				target = FindNodeByCommentIdRecursive(
					root,
					commentId.ToString());

				if (target != null)
					break;
			}

			if (target == null)
			{
				System.Diagnostics.Debug.WriteLine(
					$"FocusCommentInTree: comment {commentId} was not found.");
				return;
			}

			// Expand the node's ancestors so the linked comment is visible.
			var ancestor = target.Parent;

			while (ancestor != null)
			{
				ancestor.IsExpanded = true;
				ancestor = ancestor.Parent;
			}

			target.IsExpanded = true;

			System.Diagnostics.Debug.WriteLine(
				$"FocusCommentInTree: found comment {commentId}.");
		}

		private TreeViewNode? FindTreeViewNodeByCommentId(TreeViewNode node, int commentId)
		{
			if (node.Content is Comment c && c.Id == commentId)
				return node;

			foreach (var child in node.Children)
			{
				var result = FindTreeViewNodeByCommentId(child, commentId);

				if (result != null)
					return result;
			}

			return null;
		}

		private void PostsList_Loaded(object sender, RoutedEventArgs e)
		{
			if (_postsScrollViewer != null)
				return;

			_postsScrollViewer = PostsList.FindDescendant<ScrollViewer>();

			if (_postsScrollViewer != null)
				_postsScrollViewer.ViewChanged += PostsScrollViewer_ViewChanged;

			PostsList.ItemsSource = _posts;
		}

		private void PostsScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
		{
			if (_postsScrollViewer == null) return;
			if (!_postsHasMore) return;
			if (_isPostsLoading) return;

			var verticalOffset = _postsScrollViewer.VerticalOffset;
			var viewportHeight = _postsScrollViewer.ViewportHeight;
			var extentHeight = _postsScrollViewer.ExtentHeight;

			bool nearBottom = (extentHeight - (verticalOffset + viewportHeight)) <= LoadThresholdPx;
			bool needMoreOnFirstLoad = _isFirstLoad && _posts.Count < _minInitialPosts;

			if (!nearBottom && !needMoreOnFirstLoad) return;

			_ = LoadPostsIncrementalAsync(_postsLoadCts?.Token ?? CancellationToken.None);
		}

		private async Task LoadPostsIncrementalAsync(CancellationToken externalCt)
		{
			await _postsLoadLock.WaitAsync();
			try
			{
				if (!_postsHasMore) return;
				_isPostsLoading = true;

				try
				{
					await RunOnUiAsync(() =>
					{
						if (PostsFooterProgress != null)
						{
							PostsFooterProgress.IsActive = true;
							PostsFooterProgress.Visibility = Visibility.Visible;
						}
					});
				}
				catch { }

				using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
				var ct = linkedCts.Token;

				// Loop: fetch batches until normal stop OR (on first load) until we have at least _minInitialPosts
				while (true)
				{
					if (ct.IsCancellationRequested) break;

					// On first load ask the API for enough IDs to cover the minimum target (avoids repeated small fetches)
					var requestCount = _isFirstLoad ? Math.Max(_postsOffset + PostsBatchSize, _minInitialPosts) : _postsOffset + PostsBatchSize;

					var result = await client.GetTopStoriesAsync(requestCount);
					System.Diagnostics.Debug.WriteLine($"GetTopStoriesAsync returned Success={result.Success} Posts.Count={result.Posts?.Count ?? 0}");

					if (ct.IsCancellationRequested) break;

					if (!result.Success)
					{
						await RunOnUiAsync(async () => await ShowErrorDialog(result.ErrorMessage ?? "Unknown error."));
						break;
					}

					try
					{
						var slice = result.Posts!.Skip(_postsOffset).Take(PostsBatchSize).ToList();
						System.Diagnostics.Debug.WriteLine($"DEBUG: slice.Count={slice.Count} _postsOffset={_postsOffset}");
						// append directly (we're on UI thread via StartPostsInitialLoadAsync)
						foreach (var p in slice) _posts.Add(p);
					
					ApplySavedFontAndIndent();

					_postsOffset += slice.Count;
					_postsHasMore = slice.Count >= PostsBatchSize && result.Posts!.Count >= _postsOffset;
					SaveLastFetchTime(DateTimeOffset.UtcNow);
					_ = SavePostsCacheAsync();

					System.Diagnostics.Debug.WriteLine($"POST-APPEND: _posts.Count={_posts.Count}; PostsList.Items.Count={PostsList.Items.Count}");

					}
					catch { }
					// If first-load and we still haven't reached the minimum, continue (if there are more)
					if (_isFirstLoad && _posts.Count < _minInitialPosts && _postsHasMore)
					{
						// small delay to let UI render and avoid tight loop
						await Task.Delay(50, ct).ConfigureAwait(false);
						continue;
					}

					// Normal behavior: stop after one batch
					break;
				}
			}
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"LoadPostsIncrementalAsync error: {ex}");
			}
			finally
			{
				_isPostsLoading = false;
				try
				{
					await RunOnUiAsync(() =>
					{
						if (PostsFooterProgress != null)
						{
							PostsFooterProgress.IsActive = false;
							PostsFooterProgress.Visibility = _postsHasMore ? Visibility.Visible : Visibility.Collapsed;
						}
					});
				}
				catch { }
				_postsLoadLock.Release();
			}
		}

		private void CancelPostsLoad()
		{
			try { _postsLoadCts?.Cancel(); } catch { }
			try { _postsLoadCts?.Dispose(); } catch { }
			_postsLoadCts = null;
		}

		private async Task ShowErrorDialog(string message)
		{
			var dialog = new ContentDialog
			{
				Title = "HNReader",
				Content = message,
				CloseButtonText = "OK",
				XamlRoot = this.Content.XamlRoot
			};

			await dialog.ShowAsync();
		}

		private async void PostsList_ItemClick(object sender, ItemClickEventArgs e)
		{
			if (e.ClickedItem is not Post post)
				return;

			SetFontButtonsEnabled(false);

			try
			{
				_ephemeralPost = null;
				_ephemeralCommentId = null;

				_currentCommentsLoadCts?.Cancel();
				_currentCommentsLoadCts?.Dispose();
				_currentCommentsLoadCts = new CancellationTokenSource();

				await LoadPostContentAsync(
					post,
					isEphemeral: false,
					focusCommentId: null,
					_currentCommentsLoadCts.Token);
			}
			catch (OperationCanceledException)
			{
				// Expected when another post is selected.
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine(
					$"PostsList_ItemClick error: {ex}");
			}
			finally
			{
				SetFontButtonsEnabled(true);
			}
		}

		private static string BuildPostMetaText(Post post)
		{
			if (post == null)
				return string.Empty;

			var author = string.IsNullOrWhiteSpace(post.By)
				? string.Empty
				: post.By;

			var score = $"{post.Score} points";
			var comments = $"{post.Descendants} comments";
			var time = post.TimeAgo;

			return string.Join(
				" • ",
				new[] { author, score, comments, time }
					.Where(s => !string.IsNullOrWhiteSpace(s)));
		}

		/// <summary>
		/// Display a Post (either from _posts or ephemeral) and its comments.
		/// This method assumes the post and comments are already fetched.
		/// It only performs UI updates and must be called on the UI thread via RunOnUiAsync.
		/// </summary>
		private async Task DisplayPostAndCommentsAsync(
			Post post,
			IReadOnlyList<Comment> comments,
			bool isEphemeral,
			int? focusCommentId = null)
		{
			if (post == null)
				return;

			await RunOnUiAsync(() =>
			{
				PostTitleText.Text =
					string.IsNullOrWhiteSpace(post.Title)
						? $"Item {post.Id}"
						: post.Title;

				PostMetaText.Text = BuildPostMetaText(post);

				var text = post.Text ?? string.Empty;
				var hasText = !string.IsNullOrWhiteSpace(text);

				PostTextLabel.Visibility =
					hasText ? Visibility.Visible : Visibility.Collapsed;

				ArticleBodyScrollViewer.Visibility =
					hasText ? Visibility.Visible : Visibility.Collapsed;

				ArticleBody.Blocks.Clear();

				if (hasText)
				{
					try
					{
						if (HtmlExtensions.LooksLikeHtml(text))
						{
							MinimalHtmlRenderer.RenderToRichTextBlock(
								ArticleBody,
								text);
						}
						else
						{
							HtmlRendererHelpers.RenderPlainTextWithNewlines(
								ArticleBody,
								text);
						}
					}
					catch (Exception ex)
					{
						System.Diagnostics.Debug.WriteLine(
							$"ArticleBody HTML rendering failed: {ex}");

						HtmlRendererHelpers.RenderPlainTextWithNewlines(
							ArticleBody,
							text);
					}

					FontSizeAdjust.ReapplyGlobalDelta(ArticleBody);
				}

				Comments.Clear();

				foreach (var comment in comments)
					Comments.Add(comment);

				CommentsTree.RootNodes.Clear();

				foreach (var comment in comments)
				{
					var node = BuildNodeFromComment(comment);

					if (node != null)
						CommentsTree.RootNodes.Add(node);
				}

				ApplySavedFontAndIndent();

				if (focusCommentId.HasValue)
				{
					FocusCommentInTree(focusCommentId.Value);
				}
				else
				{
					// The TreeView may not have materialized its visual children yet.
					// Defer the scroll until the next UI/layout pass.
					_ = DispatcherQueue.TryEnqueue(() =>
					{
						ScrollCommentsTreeToTop();

						// One additional pass handles virtualization/materialization.
						_ = DispatcherQueue.TryEnqueue(() =>
						{
							ScrollCommentsTreeToTop();
						});
					});
				}
			});
		}

		private static string ShortenForHeader(string? text, int max = 120)
		{
			if (string.IsNullOrEmpty(text)) return string.Empty;
			var s = System.Net.WebUtility.HtmlDecode(text).Replace("\n", " ").Trim();
			if (s.Length <= max) return s;
			return s.Substring(0, max - 1) + "…";
		}

		private async Task LoadPostContentAsync(
	Post post,
	bool isEphemeral,
	int? focusCommentId,
	CancellationToken ct,
	bool forceNetwork = false)
		{
			if (post == null)
				return;

			ShowCommentsLoadingUI();

			try
			{
				List<Comment> comments = new();

				// For normal posts, prefer the cached tree when available.
				if (!isEphemeral &&
					!forceNetwork &&
					_cachedComments.TryGetValue(
						post.Id,
						out var cachedFlat) &&
					cachedFlat != null &&
					cachedFlat.Count > 0)
				{
					comments = ReconstructCommentsFromCache(cachedFlat);

					System.Diagnostics.Debug.WriteLine(
						$"Using cached comments for post {post.Id}: {cachedFlat.Count} items.");
				}
				else
				{
					ct.ThrowIfCancellationRequested();

					comments = await client
						.GetCommentsTreeAsync(post)
						.ConfigureAwait(false);

					System.Diagnostics.Debug.WriteLine(
						$"Fetched comments for post {post.Id}: {comments.Count} top-level nodes.");
				}

				ct.ThrowIfCancellationRequested();

				await DisplayPostAndCommentsAsync(
					post,
					comments,
					isEphemeral,
					focusCommentId);

				// Keep the fetched tree in the offline cache for normal posts.
				if (!isEphemeral && comments.Count > 0)
				{
					CacheCommentTree(post.Id, comments);
				}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine(
					$"LoadPostContentAsync failed for post {post.Id}: {ex}");

				await RunOnUiAsync(() =>
				{
					Comments.Clear();
					CommentsTree.RootNodes.Clear();

					CommentsTree.RootNodes.Add(
						new TreeViewNode
						{
							Content = new TextBlock
							{
								Text = "Failed to load comments.",
								Margin = new Thickness(6)
							}
						});
				});
			}
			finally
			{
				HideCommentsLoadingUI();
			}
		}

		private void CacheCommentTree(int postId, IEnumerable<Comment> comments)
		{
			var flat = new List<CommentCacheItem>();

			void Walk(Comment comment, int? parentId)
			{
				if (comment == null)
					return;

				flat.Add(new CommentCacheItem
				{
					Id = comment.Id,
					ParentId = parentId,
					By = comment.By,
					Text = comment.Text,
					Time = comment.Time,
					ChildIds = comment.Children?
						.Select(c => c.Id)
						.ToList()
				});

				if (comment.Children == null)
					return;

				foreach (var child in comment.Children)
					Walk(child, comment.Id);
			}

			foreach (var root in comments)
				Walk(root, null);

			lock (_cachedComments)
			{
				_cachedComments[postId] = flat;
			}
		}

		private List<Comment> ReconstructCommentsFromCache(List<CommentCacheItem> flat)
		{
			if (flat == null || flat.Count == 0) return new List<Comment>();

			// 1) create Comment instances for every cached item
			var map = new Dictionary<int, Comment>(flat.Count);
			foreach (var ci in flat)
			{
				var c = new Comment
				{
					Id = ci.Id,
					By = ci.By,
					Text = ci.Text ?? string.Empty,
					Time = ci.Time,
					Children = new List<Comment>()
				};
				map[ci.Id] = c;
			}

			// 2) attach children using ChildIds when available
			var referencedAsChild = new HashSet<int>();
			foreach (var ci in flat)
			{
				if (ci.ChildIds == null || ci.ChildIds.Count == 0) continue;

				if (!map.TryGetValue(ci.Id, out var parent)) continue;

				foreach (var childId in ci.ChildIds)
				{
					if (map.TryGetValue(childId, out var child))
					{
						parent.Children.Add(child);
						referencedAsChild.Add(childId);
					}
				}
			}

			// 3) For any items that didn't have ChildIds but have ParentId, attach them
			//    (best-effort: some caches may include ParentId)
			foreach (var ci in flat)
			{
				if (!ci.ParentId.HasValue) continue;
				if (!map.TryGetValue(ci.Id, out var child)) continue;
				if (!map.TryGetValue(ci.ParentId.Value, out var parent)) continue;

				// avoid duplicate attachment if already attached via ChildIds
				if (!parent.Children.Contains(child))
				{
					parent.Children.Add(child);
					referencedAsChild.Add(child.Id);
				}
			}

			// 4) roots are those not referenced as a child of anyone
			var roots = new List<Comment>();
			foreach (var kv in map)
			{
				if (!referencedAsChild.Contains(kv.Key))
				{
					roots.Add(kv.Value);
				}
			}

			// If nothing was marked as root (defensive), treat all as roots
			if (roots.Count == 0) roots = map.Values.ToList();

			return roots;
		}

		private void ScrollCommentsTreeToTop()
		{
			try
			{
				var scrollViewer = FindDescendant<ScrollViewer>(CommentsTree);

				if (scrollViewer != null)
				{
					scrollViewer.ChangeView(
						null,
						0,
						null,
						true);

					return;
				}
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine(
					$"ScrollCommentsTreeToTop failed: {ex}");
			}

			// Fallback for a not-yet-realized TreeView.
			ScrollFirstCommentIntoView();
		}

		private async void OpenUrlButton_Click(object sender, RoutedEventArgs e)
		{
			var tag = (sender as FrameworkElement)?.Tag as string;
			if (string.IsNullOrWhiteSpace(tag)) return;
			if (Uri.TryCreate(tag, UriKind.Absolute, out var uri))
			{
				await Windows.System.Launcher.LaunchUriAsync(uri);
			}
		}

		private void CommentsTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
		{
			sender.SelectionMode = TreeViewSelectionMode.None; // disable selection highlight
			return;
		}
		// Build a TreeViewNode from a Comment model (Content = Comment, not a UIElement)
		// Replace your existing BuildNodeFromComment with this
		private TreeViewNode? BuildNodeFromComment(Comment c)
		{
			if (c == null) return null;

			// If this comment has no author and no text and no children, skip rendering it.
			bool hasAuthor = !string.IsNullOrWhiteSpace(c.By);
			bool hasText = !string.IsNullOrWhiteSpace(c.Text);
			bool hasChildren = c.Children != null && c.Children.Count > 0;

			if (!hasAuthor && !hasText && !hasChildren)
			{
				// Skip this node entirely
				return null;
			}

			// Create the TreeViewNode and set content to the Comment model (existing behavior)
			var node = new TreeViewNode
			{
				Content = c,
				IsExpanded = c.IsExpanded
			};

			// Recursively add children, but only add child nodes that are not null (i.e., not skipped)
			if (c.Children != null)
			{
				foreach (var child in c.Children)
				{
					var childNode = BuildNodeFromComment(child);
					if (childNode != null)
						node.Children.Add(childNode);
				}
			}

			return node;
		}

		// Populate the TreeView.RootNodes from a list of top-level comments
		private async Task PopulateCommentsTree(IEnumerable<Comment> topLevelComments)
		{
			await RunOnUiAsync(() =>
			{
				CommentsTree.RootNodes.Clear();

				foreach (var c in topLevelComments)
				{
					var node = BuildNodeFromComment(c);
					if (node != null) CommentsTree.RootNodes.Add(node);
				}
				ApplySavedFontAndIndent();
				ScrollFirstCommentIntoView();
			});
		}

		private void ScrollFirstCommentIntoView()
		{
			// Try to find the first TreeViewItem visual under the comments TreeView
			var firstItem = FindDescendant<TreeViewItem>(CommentsTree);
			if (firstItem != null)
			{
				try
				{
					// No animation, immediate
					firstItem.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
					return;
				}
				catch { /* ignore */ }
			}

			// Last resort: find any UIElement child and bring it into view
			var anyChild = FindDescendant<FrameworkElement>(CommentsTree);
			if (anyChild != null)
			{
				try { anyChild.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false }); }
				catch { /* ignore */ }
			}
		}

		private static Brush GetThemeBrush(string key)
		{
			if (Application.Current?.Resources?.TryGetValue(key, out var value) == true && value is Brush brush)
				return brush;

			return new SolidColorBrush(Microsoft.UI.Colors.Black);
		}

		private void DecreaseFontSize_Click(object sender, RoutedEventArgs e)
		{
			var fontCurrent = FontSizeAdjust.GetGlobalDelta(RootElement);
			var fontNext = Math.Max(MinFontDelta, fontCurrent - FontStep);
			FontSizeAdjust.SetGlobalDelta(RootElement, fontNext);

			var indentCurrent = IndentAdjust.GetGlobalIndentDelta(RootElement);
			var indentNext = Math.Max(MinIndentDelta, indentCurrent - (FontStep * IndentStepPerFontPoint));
			IndentAdjust.SetGlobalIndentDelta(RootElement, indentNext);

			SaveSetting("FontDelta", fontNext);
			SaveSetting("IndentDelta", indentNext);
		}

		private void IncreaseFontSize_Click(object sender, RoutedEventArgs e)
		{
			// Font
			var fontCurrent = FontSizeAdjust.GetGlobalDelta(RootElement);
			var fontNext = Math.Min(MaxFontDelta, fontCurrent + FontStep);
			FontSizeAdjust.SetGlobalDelta(RootElement, fontNext);

			// Indent: map font step to indent delta
			var indentCurrent = IndentAdjust.GetGlobalIndentDelta(RootElement);
			var indentNext = Math.Min(MaxIndentDelta, indentCurrent + (FontStep * IndentStepPerFontPoint));
			IndentAdjust.SetGlobalIndentDelta(RootElement, indentNext);

			// Persist if you persist font delta
			SaveSetting("FontDelta", fontNext);
			SaveSetting("IndentDelta", indentNext);
		}

		private void ResetFontSize_Click(object sender, RoutedEventArgs e)
		{
			FontSizeAdjust.SetGlobalDelta(RootElement, 0.0);
			IndentAdjust.SetGlobalIndentDelta(RootElement, 0.0);
			SaveSetting("FontDelta", 0.0);
			SaveSetting("IndentDelta", 0.0);
		}

		private void CommentRichText_Loaded(object sender, RoutedEventArgs e)
		{
			if (sender is not RichTextBlock rtb)
				return;

			Comment? model = rtb.Tag as Comment
				?? rtb.DataContext as Comment;

			if (model == null)
			{
				FontSizeAdjust.ReapplyGlobalDelta(rtb);
				return;
			}

			try
			{
				// Render the actual HN comment HTML. This preserves links,
				// formatting, line breaks, and deleted/dead-comment handling.
				HtmlExtensions.RenderComment(rtb, model);

				FontSizeAdjust.ReapplyGlobalDelta(rtb);
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine(
					$"CommentRichText_Loaded render failed for comment {model.Id}: {ex}");

				try
				{
					rtb.Blocks.Clear();
					HtmlRendererHelpers.RenderPlainTextWithNewlines(
						rtb,
						model.Text ?? string.Empty);

					FontSizeAdjust.ReapplyGlobalDelta(rtb);
				}
				catch (Exception fallbackEx)
				{
					System.Diagnostics.Debug.WriteLine(
						$"CommentRichText_Loaded fallback failed for comment {model.Id}: {fallbackEx}");
				}
			}
		}
		private void CommentsTree_Loaded(object sender, RoutedEventArgs e)
		{
			if (sender is FrameworkElement root)
			{
				IndentAdjust.ReapplyGlobalIndent(root);
				// Run once immediately
				RaiseTreeViewChevronZIndex(root);

				// Re-run when layout changes (virtualization/materialization). Debounced to avoid thrash.
				root.LayoutUpdated -= CommentsTree_LayoutUpdated;
				root.LayoutUpdated += CommentsTree_LayoutUpdated;
			}
		}

		private void CommentsTree_LayoutUpdated(object? sender, object? e)
		{
			// If we're shutting down, ignore layout updates entirely
			if (_isClosing) return;

			// Debounce: cancel previous scheduled run and schedule a new one 100ms later
			// Use SafeCancelDispose to avoid cancelling a disposed CTS
			SafeCancelDispose(ref _chevronCts);

			// Create a fresh CTS for the debounce window
			_chevronCts = new CancellationTokenSource();
			var token = _chevronCts.Token;

			_ = Task.Run(async () =>
			{
				try
				{
					await Task.Delay(100, token).ConfigureAwait(false);
					// marshal back to UI thread
					_ = DispatcherQueue.TryEnqueue(() =>
					{
						// If closing started while we were waiting, skip the work
						if (_isClosing) return;
						// 'sender' may not be a FrameworkElement; guard it
						if (sender is FrameworkElement root)
							RaiseTreeViewChevronZIndex(root);
					});
				}
				catch (OperationCanceledException) { /* expected on cancel */ }
				catch (ObjectDisposedException) { /* defensive: ignore if CTS disposed concurrently */ }
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine($"CommentsTree_LayoutUpdated background error: {ex}");
				}
			}, token);
		}

		private void RaiseTreeViewChevronZIndex(FrameworkElement root)
		{
			if (root == null || _isClosing) return;

			// Try to obtain a ZIndex DependencyProperty via reflection from likely types
			DependencyProperty? zIndexDp = null;
			Type?[] candidateTypes = new[]
			{
				// common locations across different WinUI/UWP versions
				Type.GetType("Microsoft.UI.Xaml.Controls.Panel, Microsoft.UI.Xaml") ?? Type.GetType("Windows.UI.Xaml.Controls.Panel, Windows"),
				Type.GetType("Microsoft.UI.Xaml.Controls.Canvas, Microsoft.UI.Xaml") ?? Type.GetType("Windows.UI.Xaml.Controls.Canvas, Windows")
			};

			foreach (var t in candidateTypes)
			{
				if (t == null) continue;
				try
				{
					var prop = t.GetProperty("ZIndexProperty", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
					if (prop != null)
					{
						var val = prop.GetValue(null) as DependencyProperty;
						if (val != null)
						{
							zIndexDp = val;
							break;
						}
					}
				}
				catch { /* ignore reflection failures */ }
			}

			// If we found a ZIndex attached DP, set it on each ToggleButton; otherwise we'll fall back to margin
			if (zIndexDp != null)
			{
				foreach (var t in FindDescendants<ToggleButton>(root))
				{
					try
					{
						t.SetValue(zIndexDp, 1000);
						t.IsHitTestVisible = true;
					}
					catch { /* ignore per-element failures */ }
				}
				return;
			}

			// Fallback: if no ZIndex DP available, ensure toggles have a small positive margin and are brought visually forward
			foreach (var t in FindDescendants<ToggleButton>(root))
			{
				try
				{
					// give the toggle a slight positive margin so it sits visually above content
					var m = t.Margin;
					// only increase left if it looks small
					if (m.Left < 2) t.Margin = new Thickness(m.Left + 2, m.Top, m.Right, m.Bottom);

					// make sure toggle is on top of siblings by reparenting it to the same parent at the end
					var parent = VisualTreeHelper.GetParent(t) as Panel;
					if (parent != null)
					{
						// move the toggle to the end of the children collection so it renders last
						try
						{
							if (parent.Children.Contains(t))
							{
								parent.Children.Remove(t);
								parent.Children.Add(t);
							}
						}
						catch { /* some parents are not Panel or don't allow manipulation; ignore */ }
					}

					t.IsHitTestVisible = true;
				}
				catch { /* ignore per-element failures */ }
			}
		}

		private void ShowCommentsLoadingUI()
		{
			try
			{
				// disable font buttons and show overlay on UI thread
				_ = RunOnUiAsync(() =>
				{
					SetFontButtonsEnabled(false);
					CommentsLoadingOverlay.Visibility = Visibility.Visible;
					CommentsLoadingOverlay.IsHitTestVisible = true; // block interaction while loading
					CommentsProgressRing.IsActive = true;
				});
			}
			catch { /* ignore during shutdown */ }
		}

		private void HideCommentsLoadingUI()
		{
			try
			{
				_ = RunOnUiAsync(() =>
				{
					CommentsProgressRing.IsActive = false;
					CommentsLoadingOverlay.Visibility = Visibility.Collapsed;
					CommentsLoadingOverlay.IsHitTestVisible = false;
					SetFontButtonsEnabled(true);
				});
			}
			catch { /* ignore during shutdown */ }
		}

		private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
		{
			if (root == null) yield break;
			var q = new Queue<DependencyObject>();
			q.Enqueue(root);

			while (q.Count > 0)
			{
				var cur = q.Dequeue();
				if (cur is T t) yield return t;

				int c = 0;
				try { c = VisualTreeHelper.GetChildrenCount(cur); }
				catch { continue; }

				for (int i = 0; i < c; i++)
				{
					DependencyObject? child = null;
					try { child = VisualTreeHelper.GetChild(cur, i); }
					catch { continue; }
					if (child != null) q.Enqueue(child);
				}
			}
		}

		private async void SaveOfflineButton_Click(object sender, RoutedEventArgs e)
		{
			// disable button while saving
			try
			{
				SaveOfflineButton.IsEnabled = false;
			}
			catch { }

			_saveOfflineCts?.Cancel();
			_saveOfflineCts?.Dispose();
			_saveOfflineCts = new CancellationTokenSource();
			var ct = _saveOfflineCts.Token;

			try
			{
				await Task.Run(() => FetchAndCacheAllCommentsAsync(ct));
				// persist posts+comments to disk
				await SavePostsCacheAsync().ConfigureAwait(false);
				System.Diagnostics.Debug.WriteLine("SaveOffline: posts+comments cached.");
			}
			catch (OperationCanceledException) { System.Diagnostics.Debug.WriteLine("SaveOffline cancelled."); }
			catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"SaveOffline failed: {ex}"); }
			finally
			{
				try { SaveOfflineButton.IsEnabled = true; } catch { }
				try { _saveOfflineCts?.Dispose(); } catch { }
				_saveOfflineCts = null;
			}
		}

		private async Task FetchAndCacheAllCommentsAsync(CancellationToken ct)
		{
			// limit concurrency to avoid hammering network
			const int MaxParallel = 6;
			var semaphore = new SemaphoreSlim(MaxParallel, MaxParallel);

			var postsSnapshot = _posts.ToList(); // snapshot of current posts
			var tasks = new List<Task>();

			foreach (var post in postsSnapshot)
			{
				await semaphore.WaitAsync(ct).ConfigureAwait(false);
				tasks.Add(Task.Run(async () =>
				{
					try
					{
						// fetch comments tree on background thread
						List<Comment> tree;
						try
						{
							tree = await client.GetCommentsTreeAsync(post).ConfigureAwait(false);
						}
						catch
						{
							tree = new List<Comment>();
						}

						// flatten tree into CommentCacheItem list with parent/child ids
						// build flat list of CommentCacheItem from the comment tree
						var flat = new List<CommentCacheItem>();

						void Walk(Comment c, int? parent)
						{
							if (c == null) return;

							// use the Comment.Id directly
							var id = c.Id;

							// prefer child ids from the Children collection
							List<int>? childIds = null;
							if (c.Children != null && c.Children.Count > 0)
							{
								childIds = c.Children.Select(ch => ch.Id).ToList();
							}

							flat.Add(new CommentCacheItem
							{
								Id = id,
								ParentId = parent,
								By = c.By,
								Text = c.Text,
								Time = c.Time,
								ChildIds = childIds
							});

							// recurse into children
							if (c.Children != null) foreach (var child in c.Children) Walk(child, id);
						}

						// If the client returned a tree, walk each top-level node
						if (tree != null && tree.Count > 0) foreach (var top in tree) Walk(top, null);

						// store into in-memory cache (thread-safe via lock)
						lock (_cachedComments)
						{
							_cachedComments[(int)post.Id] = flat;
						}
						var descendantsCount = flat?.Count ?? 0;
						await RunOnUiAsync(() =>
						{
							var existing = _posts.FirstOrDefault(x => x.Id == post.Id);
							existing?.Descendants = descendantsCount;
						});
						SaveLastFetchTime(DateTimeOffset.UtcNow);
						_ = SavePostsCacheAsync();
					}
					catch (Exception ex)
					{
						System.Diagnostics.Debug.WriteLine($"Fetch comments for post {post.Id} failed: {ex}");
					}
					finally
					{
						semaphore.Release();
					}
				}, ct));
			}
			await Task.WhenAll(tasks).ConfigureAwait(false);
		}
	}
}
