// HNReader.Backend.cs
// UI-free backend for Hacker News account/session actions.
// Focus: login, account/session management, voting, favorites, and replying.
// This is designed to sit next to the existing HNClient read model.
//
// Plan:
// 1) HnBackendClient wires the existing read-only HNClient to write-capable services.
// 2) HnAccountService handles login/session persistence.
// 3) HnWriteService performs vote/favorite/reply/comment actions through Hacker News web forms.
// 4) HnHtmlFormHelper centralizes the page scraping logic so the UI never knows about it.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HNReader;

public sealed class HnBackendOptions
{
	public Uri BaseUri { get; init; } = new("https://news.ycombinator.com/");
	public string UserAgent { get; init; } = "HNReader/1.0";
	public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
	public TimeSpan WriteThrottleDelay { get; init; } = TimeSpan.FromMilliseconds(250);
	public int MaxWriteConcurrency { get; init; } = 1;
	public bool UseProxy { get; init; } = true;
}

public sealed record HnSessionSnapshot(
	string? UserName,
	string? CookieHeader,
	DateTimeOffset SavedAtUtc);

public interface IHnSessionStore
{
	Task<HnSessionSnapshot?> LoadAsync(CancellationToken ct = default);
	Task SaveAsync(HnSessionSnapshot snapshot, CancellationToken ct = default);
	Task ClearAsync(CancellationToken ct = default);
}

public sealed class InMemoryHnSessionStore : IHnSessionStore
{
	private HnSessionSnapshot? _snapshot;

	public Task<HnSessionSnapshot?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_snapshot);

	public Task SaveAsync(HnSessionSnapshot snapshot, CancellationToken ct = default)
	{
		_snapshot = snapshot;
		return Task.CompletedTask;
	}

	public Task ClearAsync(CancellationToken ct = default)
	{
		_snapshot = null;
		return Task.CompletedTask;
	}
}

public sealed class JsonFileHnSessionStore(string path) : IHnSessionStore
{
	private readonly string _path = path ?? throw new ArgumentNullException(nameof(path));

	public async Task<HnSessionSnapshot?> LoadAsync(CancellationToken ct = default)
	{
		if (!File.Exists(_path))
			return null;

		await using var stream = File.OpenRead(_path);
		return await JsonSerializer.DeserializeAsync<HnSessionSnapshot>(stream, cancellationToken: ct)
			.ConfigureAwait(false);
	}

	public async Task SaveAsync(HnSessionSnapshot snapshot, CancellationToken ct = default)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
		await using var stream = File.Create(_path);
		await JsonSerializer.SerializeAsync(stream, snapshot, cancellationToken: ct).ConfigureAwait(false);
	}

	public Task ClearAsync(CancellationToken ct = default)
	{
		if (File.Exists(_path))
			File.Delete(_path);

		return Task.CompletedTask;
	}
}

public sealed record HnLoginResult(
	bool Success,
	string? UserName,
	HttpStatusCode? StatusCode,
	string? Message,
	string? CookieHeader);

public sealed record HnActionResult(
	bool Success,
	HttpStatusCode? StatusCode,
	string? Message,
	string? ResponseHtml,
	Uri? ActionUri = null,
	bool? HasUpvoted = null);

public enum HnTargetKind
{
	Story,
	Comment,
	Poll,
	PollOption
}

public sealed record HnCommentDraft(int ParentId, string Text);

public sealed class HnBackendClient : IAsyncDisposable
{
	private readonly HNClient _reader;
	private readonly HnWebSessionClient _web;
	private readonly IHnSessionStore _sessionStore;
	private readonly SemaphoreSlim _writeGate;
	private readonly TimeSpan _writeThrottleDelay;

	public HnAccountService Accounts { get; }
	public HnWriteService Write { get; }

	public HnBackendClient(
		HNClient reader,
		HnBackendOptions? options = null,
		IHnSessionStore? sessionStore = null)
	{
		_reader = reader ?? throw new ArgumentNullException(nameof(reader));
		options ??= new HnBackendOptions();
		_sessionStore = sessionStore ?? new InMemoryHnSessionStore();
		_writeGate = new SemaphoreSlim(Math.Max(1, options.MaxWriteConcurrency), Math.Max(1, options.MaxWriteConcurrency));
		_writeThrottleDelay = options.WriteThrottleDelay;

		_web = new HnWebSessionClient(options);
		Accounts = new HnAccountService(_web, _sessionStore);
		Write = new HnWriteService(_web, _writeGate, _writeThrottleDelay);
	}

	public string? CurrentUserName => Accounts.CurrentUserName;
	public bool IsAuthenticated => Accounts.IsAuthenticated;

	public Task InitializeAsync(CancellationToken ct = default) => Accounts.InitializeAsync(ct);

	public Task<Post?> GetStoryAsync(int id, CancellationToken ct = default)
		=> _reader.GetItemAsync(id, ct);

	public Task<List<Comment>> GetCommentsAsync(Post post, CancellationToken ct = default)
		=> _reader.GetCommentsTreeAsync(post, ct);

	public IAsyncEnumerable<Comment> StreamCommentsAsync(Post post, CancellationToken ct = default)
		=> _reader.GetCommentsStreamAsync(post, ct);

	public Task<HNResult> GetTopStoriesAsync(int limit = 30, CancellationToken ct = default)
		=> _reader.GetTopStoriesAsync(limit, ct);

	public async Task<Dictionary<int, VoteInfo>> GetCurrentPostVoteInfoAsync(CancellationToken ct = default)
	{
		if (!IsAuthenticated)
			return [];

		var html = await _web.GetStringAsync("news", ct).ConfigureAwait(false);

		return _web.GetVoteInfo(html);
	}

	public async Task ApplyVoteInfoAsync(
	Post post,
	CancellationToken ct = default)
	{
		if (!_web.GetCookies().Any())
		{
			post.CanVote = false;
			post.HasUpvoted = false;
			return;
		}

		var html = await _web.GetStringAsync(
			$"item?id={post.Id}",
			ct).ConfigureAwait(false);

		if (HnWebSessionClient.TryGetVoteInfo(
			html,
			post.Id,
			out var auth,
			out var hasUpvoted))
		{
			post.CanVote = true;
			post.HasUpvoted = hasUpvoted;
		}
		else
		{
			post.CanVote = false;
			post.HasUpvoted = false;
		}
	}

	public async Task ApplyPostVoteInfoAsync(
		Post post,
		CancellationToken ct = default)
	{
		ArgumentNullException.ThrowIfNull(post);

		if (!IsAuthenticated)
		{
			post.CanVote = false;
			post.HasUpvoted = false;
			return;
		}

		var html = await _web.GetStringAsync(
			$"item?id={post.Id}",
			ct).ConfigureAwait(false);

		if (HnWebSessionClient.TryGetVoteInfo(
			html,
			post.Id,
			out _,
			out var hasUpvoted))
		{
			post.CanVote = true;
			post.HasUpvoted = hasUpvoted;
		}
		else
		{
			post.CanVote = false;
			post.HasUpvoted = false;
		}
	}
	public ValueTask DisposeAsync()
	{
		_web.Dispose();
		_writeGate.Dispose();
		return ValueTask.CompletedTask;
	}
}

public sealed class HnAccountService
{
	private readonly HnWebSessionClient _web;
	private readonly IHnSessionStore _sessionStore;

	public string? CurrentUserName { get; private set; }
	public bool IsAuthenticated { get; private set; }

	internal HnAccountService(HnWebSessionClient web, IHnSessionStore sessionStore)
	{
		_web = web;
		_sessionStore = sessionStore;
	}

	public async Task InitializeAsync(CancellationToken ct = default)
	{
		var snapshot = await _sessionStore.LoadAsync(ct).ConfigureAwait(false);
		if (snapshot != null)
		{
			CurrentUserName = snapshot.UserName;
			IsAuthenticated = !string.IsNullOrWhiteSpace(snapshot.CookieHeader);
			if (!string.IsNullOrWhiteSpace(snapshot.CookieHeader))
				_web.ImportCookieHeader(snapshot.CookieHeader);
		}
	}

	private static string Preview(string? value, int max = 800)
	{
		if (string.IsNullOrEmpty(value))
			return "<empty>";

		return value.Length <= max ? value : value[..max];
	}

	public async Task<HnLoginResult> LoginAsync(
		string userName,
		string password,
		CancellationToken ct = default)
	{
		userName = (userName ?? string.Empty).Trim();
		password = password ?? string.Empty;

		if (string.IsNullOrWhiteSpace(userName))
			return new HnLoginResult(false, null, null, "User name is required.", null);

		Debug.WriteLine("[LOGIN] GET /login");

		var loginPage = await _web.GetStringAsync("login", ct)
			.ConfigureAwait(false);

		var form = HnHtmlFormHelper.FindFirstForm(loginPage, _web.BaseUri);

		if (form is null)
			return new HnLoginResult(false, null, null, "Login form not found.", null);

		Debug.WriteLine($"[LOGIN] form action = {form.ActionUri}");
		Debug.WriteLine($"[LOGIN] form method = {form.Method}");
		Debug.WriteLine($"[LOGIN] form fields = {string.Join(", ", form.Fields.Keys)}");

		form.SetField("acct", userName);
		form.SetField("pw", password);
		form.SetField("goto", "news");

		var response = await _web.SubmitAsync(form, ct)
			.ConfigureAwait(false);
		var cookieHeader = _web.GetCookieHeader();

		Debug.WriteLine(
			$"[LOGIN] cookies = {string.Join("; ", _web.GetCookies())}");

		Debug.WriteLine(
			$"[LOGIN] submit status = {(int)response.StatusCode} {response.StatusCode}");

		Debug.WriteLine(
			$"[LOGIN] submit body preview = {Preview(response.Html)}");

		Debug.WriteLine(
			$"[LOGIN] cookie header = " +
			$"{(string.IsNullOrWhiteSpace(cookieHeader) ? "<empty>" : cookieHeader)}");

		var newsHtml = await _web.GetStringAsync("news", ct)
			.ConfigureAwait(false);

		var hasLogout =
			HnStringHelpers.ContainsIgnoreCase(newsHtml, "logout");

		Debug.WriteLine($"[LOGIN] /news contains logout = {hasLogout}");
		Debug.WriteLine($"[LOGIN] /news preview = {Preview(newsHtml)}");

		var looksFailed =
			HnStringHelpers.ContainsIgnoreCase(response.Html, "bad login") ||
			HnStringHelpers.ContainsIgnoreCase(response.Html, "wrong password") ||
			HnStringHelpers.ContainsIgnoreCase(response.Html, "please try again");

		var success =
			!looksFailed &&
			!string.IsNullOrWhiteSpace(cookieHeader) &&
			hasLogout;

		if (success)
		{
			CurrentUserName = userName;
			IsAuthenticated = true;

			await _sessionStore.SaveAsync(
				new HnSessionSnapshot(
					userName,
					cookieHeader,
					DateTimeOffset.UtcNow),
				ct).ConfigureAwait(false);
		}

		return new HnLoginResult(
			success,
			success ? userName : null,
			response.StatusCode,
			success ? "Logged in." : "Login failed.",
			success ? cookieHeader : null);
	}

	public async Task LogoutAsync(CancellationToken ct = default)
	{
		// Clear local state first; if the site has changed its logout flow, the local session is still cleared.
		CurrentUserName = null;
		IsAuthenticated = false;
		await _sessionStore.ClearAsync(ct).ConfigureAwait(false);

		try
		{
			await _web.GetStringAsync("logout", ct).ConfigureAwait(false);
		}
		catch
		{
			// Intentionally ignored: local logout is the important part for the backend.
		}
	}

	public async Task RefreshSessionAsync(CancellationToken ct = default)
	{
		// A soft refresh: ask the site for a page that should reflect the current session cookie.
		var html = await _web.GetStringAsync("news", ct).ConfigureAwait(false);
		IsAuthenticated = HnStringHelpers.ContainsIgnoreCase(html, "logout");
		if (!IsAuthenticated)
			CurrentUserName = null;
	}
}

public sealed class HnWriteService
{
	private readonly HnWebSessionClient _web;
	private readonly SemaphoreSlim _gate;
	private readonly TimeSpan _throttleDelay;

	internal HnWriteService(HnWebSessionClient web, SemaphoreSlim gate, TimeSpan throttleDelay)
	{
		_web = web;
		_gate = gate;
		_throttleDelay = throttleDelay;
	}

	/// <summary>
	/// Casts (or retracts) an upvote on a story or comment.
	/// </summary>
	/// <param name="up">
	/// true to upvote the item; false to retract an existing upvote ("un-vote").
	/// Downvoting is not supported here: HN only shows a down arrow to accounts with
	/// enough karma, and doing it safely needs its own explicit path, not an overload
	/// of this one.
	/// </param>
	public async Task<HnActionResult> VoteAsync(
		int itemId,
		HnTargetKind targetKind,
		bool up,
		CancellationToken ct = default)
	{
		await _gate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			var html = await _web.GetStringAsync($"item?id={itemId}", ct).ConfigureAwait(false);

			// Vote links carry a per-item, per-user auth token; without it HN's vote
			// endpoint rejects the request outright, so a URL built from the id alone
			// is never enough. TryGetVoteInfo reads that token straight off the vote
			// arrow HN itself rendered for this item.
			//
			// Known limitation: if an account has enough karma to see both an up and a
			// down arrow on the same item, TryGetVoteInfo returns whichever of the two
			// it encounters first in the markup, not necessarily the "up" one. For
			// upvote-only accounts (the common case) only one arrow ever exists, so this
			// doesn't come up. Extend TryGetVoteInfo to disambiguate by `how=` if you add
			// downvoting.
			if (!HnWebSessionClient.TryGetVoteInfo(html, itemId, out var auth, out _))
			{
				return new HnActionResult(
					false,
					null,
					"Vote control not found for this item (not signed in, self-authored, voting closed, or the item id is wrong).",
					html);
			}

			var how = up ? "up" : "un";
			var gotoValue = Uri.EscapeDataString($"item?id={itemId}");

			var voteUri = new Uri(
				_web.BaseUri,
				$"vote?id={itemId}" +
				$"&how={how}" +
				$"&auth={Uri.EscapeDataString(auth)}" +
				$"&goto={gotoValue}");

			await _web.GetStringAsync(voteUri.AbsoluteUri, ct).ConfigureAwait(false);

			await Task.Delay(_throttleDelay, ct)
				.ConfigureAwait(false);

			var verificationHtml =
				await _web.GetStringAsync(
					$"item?id={itemId}",
					ct)
				.ConfigureAwait(false);

			var confirmed =
				HnWebSessionClient.TryGetVoteInfo(
					verificationHtml,
					itemId,
					out _,
					out var nowUpvoted) &&
				nowUpvoted == up;

			return new HnActionResult(
				confirmed,
				HttpStatusCode.OK,
				confirmed ? "Vote submitted." : "Vote request sent, but the new state could not be confirmed.",
				verificationHtml,
				voteUri,
				HasUpvoted: confirmed ? up : (bool?)null);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async Task<HnActionResult> FavoriteAsync(int itemId, CancellationToken ct = default)
	{
		await _gate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			var html = await _web.GetStringAsync($"item?id={itemId}", ct).ConfigureAwait(false);
			var favoriteUri = HnHtmlFormHelper.FindFirstLink(html, _web.BaseUri, $"favorite?id={itemId}");
			if (favoriteUri is null)
				return new HnActionResult(false, null, "Favorite link not found.", html);

			var response = await _web.GetStringAsync(favoriteUri.AbsoluteUri, ct).ConfigureAwait(false);
			await Task.Delay(_throttleDelay, ct).ConfigureAwait(false);

			return new HnActionResult(true, HttpStatusCode.OK, "Favorite submitted.", response, favoriteUri);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async Task<HnActionResult> ReplyAsync(int parentId, string text, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(text))
			return new HnActionResult(false, null, "Reply text is empty.", null);

		await _gate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			var replyPage = await _web.GetStringAsync($"reply?id={parentId}", ct).ConfigureAwait(false);
			var form = HnHtmlFormHelper.FindFirstForm(replyPage, _web.BaseUri);
			if (form is null)
				return new HnActionResult(false, null, "Reply form not found.", replyPage);

			form.SetField("text", text.Trim());

			// Keep the parent if the form exposes it; if not, the page form will usually submit the correct context anyway.
			if (!form.Fields.ContainsKey("parent"))
				form.SetField("parent", parentId.ToString());

			var response = await _web.SubmitAsync(form, ct).ConfigureAwait(false);
			await Task.Delay(_throttleDelay, ct).ConfigureAwait(false);

			var looksFailed =
				HnStringHelpers.ContainsIgnoreCase(response.Html, "please log in") ||
				HnStringHelpers.ContainsIgnoreCase(response.Html, "you can't") ||
				HnStringHelpers.ContainsIgnoreCase(response.Html, "error");

			return new HnActionResult(!looksFailed, response.StatusCode, looksFailed ? "Reply rejected." : "Reply submitted.", response.Html, form.ActionUri);
		}
		finally
		{
			_gate.Release();
		}
	}

	public Task<HnActionResult> CommentOnStoryAsync(int storyId, string text, CancellationToken ct = default)
		=> ReplyAsync(storyId, text, ct);
}

internal sealed partial class HnWebSessionClient : IDisposable
{
	private readonly CookieContainer _cookies = new();
	private readonly HttpClientHandler _handler;
	private readonly HttpClient _http;

	public Uri BaseUri { get; }

	public HnWebSessionClient(HnBackendOptions options)
	{
		BaseUri = options.BaseUri;
		_handler = new HttpClientHandler
		{
			AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
			UseCookies = true,
			CookieContainer = _cookies,
			UseProxy = options.UseProxy,
			DefaultProxyCredentials = CredentialCache.DefaultCredentials
		};

		_http = new HttpClient(_handler)
		{
			BaseAddress = BaseUri,
			Timeout = options.RequestTimeout
		};
		_http.DefaultRequestHeaders.UserAgent.Clear();
		_http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HNReader", "1.0"));
	}

	public void ImportCookieHeader(string cookieHeader)
	{
		if (string.IsNullOrWhiteSpace(cookieHeader))
			return;

		foreach (var pair in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var index = pair.IndexOf('=');
			if (index <= 0)
				continue;

			var name = pair[..index].Trim();
			var value = pair[(index + 1)..].Trim();

			try
			{
				_cookies.Add(BaseUri, new Cookie(name, value));
			}
			catch
			{
				// Ignore malformed cookies; if HN changes the cookie set, a partial session is still better than none.
			}
		}
	}

	public string GetCookieHeader() => _cookies.GetCookieHeader(BaseUri);

	public async Task<string> GetStringAsync(string relativeOrAbsolute, CancellationToken ct = default)
	{
		using var resp = await _http.GetAsync(
			MakeUri(relativeOrAbsolute),
			HttpCompletionOption.ResponseHeadersRead,
			ct).ConfigureAwait(false);

		return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
	}

	public IReadOnlyList<string> GetCookies()
	{
		return _cookies
			.GetCookies(BaseUri)
			.Cast<Cookie>()
			.Select(c => $"{c.Name}={c.Value}")
			.ToArray();
	}

	public async Task<HnHttpResponse> SubmitAsync(HnHtmlForm form, CancellationToken ct = default)
	{
		ArgumentNullException.ThrowIfNull(form);

		using var content = new FormUrlEncodedContent(form.Fields.Select(kvp => new KeyValuePair<string, string>(kvp.Key, kvp.Value)));

		HttpResponseMessage resp;
		if (string.Equals(form.Method, "get", StringComparison.OrdinalIgnoreCase))
		{
			var query = string.Join("&", form.Fields.Select(kvp =>
				Uri.EscapeDataString(kvp.Key) + "=" + Uri.EscapeDataString(kvp.Value)));
			var uri = form.ActionUri;
			var builder = new UriBuilder(uri);
			if (string.IsNullOrWhiteSpace(builder.Query))
				builder.Query = query;
			else
				builder.Query = builder.Query.TrimStart('?') + "&" + query;

			resp = await _http.GetAsync(builder.Uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
		}
		else
		{
			var actionUri = form.ActionUri;

			if (actionUri.Host.Equals("news.ycombinator.com", StringComparison.OrdinalIgnoreCase) &&
				actionUri.AbsolutePath == "/")
			{
				actionUri = new Uri(BaseUri, "login");
			}

			resp = await _http.PostAsync(actionUri, content, ct)
				.ConfigureAwait(false);
		}

		using (resp)
		{
			var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
			return new HnHttpResponse(resp.StatusCode, html);
		}
	}
	private Uri MakeUri(string relativeOrAbsolute)
	{
		if (string.IsNullOrWhiteSpace(relativeOrAbsolute))
			return BaseUri;

		if (Uri.TryCreate(relativeOrAbsolute, UriKind.Absolute, out var absolute))
			return absolute;

		return new Uri(BaseUri, relativeOrAbsolute);
	}

	public static bool TryGetVoteInfo(
	string html,
	int itemId,
	out string auth,
	out bool hasUpvoted)
	{
		return HnHtmlFormHelper.TryGetVoteInfo(
			html,
			itemId,
			out auth,
			out hasUpvoted);
	}

	public Dictionary<int, VoteInfo> GetVoteInfo(string html)
	{
		return HnHtmlFormHelper.GetVoteInfo(html);
	}

	public void Dispose()
	{
		_http.Dispose();
		_handler.Dispose();
	}
}

internal sealed record HnHttpResponse(HttpStatusCode StatusCode, string Html);

internal sealed class HnHtmlForm(Uri actionUri, string method, Dictionary<string, string> fields)
{
	public Uri ActionUri { get; } = actionUri;
	public string Method { get; } = string.IsNullOrWhiteSpace(method) ? "post" : method.Trim();
	public Dictionary<string, string> Fields { get; } = fields;

	public void SetField(string name, string value) => Fields[name] = value;
}

public readonly record struct VoteInfo(
	string Auth,
	bool HasUpvoted);

internal static class HnHtmlFormHelper
{
	private static readonly Regex FormRegex = new(
		@"<form\b(?<attrs>[^>]*)>(?<body>.*?)</form>",
		RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

	private static readonly Regex InputRegex = new(
		@"<input\b(?<attrs>[^>]*)>",
		RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

	private static readonly Regex TextareaRegex = new(
		@"<textarea\b(?<attrs>[^>]*)>(?<value>.*?)</textarea>",
		RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

	private static readonly Regex LinkRegex = new(
		@"href\s*=\s*[""'](?<href>[^""']+)[""']",
		RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

	public static HnHtmlForm? FindFirstForm(string html, Uri baseUri)
	{
		if (string.IsNullOrWhiteSpace(html))
			return null;

		var match = FormRegex.Match(html);
		if (!match.Success)
			return null;

		var attrs = match.Groups["attrs"].Value;
		var body = match.Groups["body"].Value;

		var action = GetAttribute(attrs, "action") ?? string.Empty;
		var method = GetAttribute(attrs, "method") ?? "post";
		var actionUri = Resolve(baseUri, action);

		var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (Match input in InputRegex.Matches(body))
		{
			var inputAttrs = input.Groups["attrs"].Value;
			var name = GetAttribute(inputAttrs, "name");
			if (string.IsNullOrWhiteSpace(name))
				continue;

			var value = GetAttribute(inputAttrs, "value") ?? string.Empty;
			fields[name] = HtmlDecode(value);
		}

		foreach (Match textArea in TextareaRegex.Matches(body))
		{
			var textAreaAttrs = textArea.Groups["attrs"].Value;
			var name = GetAttribute(textAreaAttrs, "name");
			if (string.IsNullOrWhiteSpace(name))
				continue;

			fields[name] = HtmlDecode(textArea.Groups["value"].Value);
		}

		return new HnHtmlForm(actionUri, method, fields);
	}

	public static Uri? FindFirstLink(string html, Uri baseUri, string contains)
	{
		if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(contains))
			return null;

		foreach (Match match in LinkRegex.Matches(html))
		{
			var href = HtmlDecode(match.Groups["href"].Value);
			if (!href.Contains(contains, StringComparison.OrdinalIgnoreCase))
				continue;

			return Resolve(baseUri, href);
		}

		return null;
	}

	private static Uri Resolve(Uri baseUri, string relativeOrAbsolute)
	{
		if (string.IsNullOrWhiteSpace(relativeOrAbsolute))
			return baseUri;

		return Uri.TryCreate(relativeOrAbsolute, UriKind.Absolute, out var absolute)
			? absolute
			: new Uri(baseUri, relativeOrAbsolute);
	}

	private static string? GetAttribute(string attrs, string name)
	{
		if (string.IsNullOrWhiteSpace(attrs) || string.IsNullOrWhiteSpace(name))
			return null;

		var regex = new Regex(
			$@"\b{Regex.Escape(name)}\s*=\s*['""](?<value>[^'""]*)['""]",
			RegexOptions.IgnoreCase | RegexOptions.Singleline);

		var match = regex.Match(attrs);
		return match.Success ? match.Groups["value"].Value : null;
	}

	private static string HtmlDecode(string value) => WebUtility.HtmlDecode(value ?? string.Empty);

	private static Dictionary<string, string> ParseQuery(string query)
	{
		var result = new Dictionary<string, string>(
			StringComparer.OrdinalIgnoreCase);

		foreach (var pair in query.TrimStart('?')
			.Split('&', StringSplitOptions.RemoveEmptyEntries))
		{
			var parts = pair.Split('=', 2);

			if (parts.Length != 2)
				continue;

			var key = Uri.UnescapeDataString(parts[0]);
			var value = Uri.UnescapeDataString(parts[1]);

			result[key] = value;
		}

		return result;
	}
	internal static Dictionary<int, VoteInfo> GetVoteInfo(string html)
	{
		var result = new Dictionary<int, VoteInfo>();

		if (string.IsNullOrWhiteSpace(html))
			return result;

		foreach (Match match in LinkRegex.Matches(html))
		{
			var href = HtmlDecode(match.Groups["href"].Value);

			if (!href.StartsWith("vote?", StringComparison.OrdinalIgnoreCase))
				continue;

			Uri uri;

			try
			{
				uri = Resolve(
					new Uri("https://news.ycombinator.com/"),
					href);
			}
			catch
			{
				continue;
			}

			var query = ParseQuery(uri.Query);

			if (!int.TryParse(
				query.GetValueOrDefault("id"),
				out var itemId))
			{
				continue;
			}

			var auth = query.GetValueOrDefault("auth");
			if (string.IsNullOrWhiteSpace(auth))
				continue;

			var how = query.GetValueOrDefault("how");

			var hasUpvoted = string.Equals(
				how,
				"un",
				StringComparison.OrdinalIgnoreCase);

			// Prefer "un" if HN exposes both links for the same item.
			if (!result.TryGetValue(itemId, out var existing) ||
				(hasUpvoted && !existing.HasUpvoted))
			{
				result[itemId] = new VoteInfo(auth, hasUpvoted);
			}
		}

		return result;
	}

	internal static bool TryGetVoteInfo(
		string html,
		int itemId,
		out string auth,
		out bool hasUpvoted)
	{
		auth = string.Empty;
		hasUpvoted = false;

		var all = GetVoteInfo(html);

		if (!all.TryGetValue(itemId, out var info))
			return false;

		auth = info.Auth;
		hasUpvoted = info.HasUpvoted;
		return true;
	}
}

internal static class HnStringHelpers
{
	public static bool ContainsIgnoreCase(string? value, string needle)
		=> !string.IsNullOrEmpty(value) && value.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
