using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GameVault.ViewModels.Services;
using static GameVault.Platform.Achievements.RcheevosNative;

namespace GameVault.Platform.Achievements;

/// <summary>Answers rcheevos' server requests: an HTTP POST in the app, canned JSON in tests.</summary>
public interface IAchievementsServer
{
    /// <summary>
    /// Sends one request. Transport failures return status <see cref="RcheevosNative.ServerResponseRetryableClientError"/>
    /// (rcheevos retries) rather than throwing.
    /// </summary>
    Task<AchievementsServerResponse> PostAsync(string url, string? postData, string? contentType);
}

public sealed record AchievementsServerResponse(int StatusCode, byte[] Body);

/// <summary>Reads emulated memory for rcheevos: fills <paramref name="buffer"/> from <paramref name="address"/>, returns bytes read.</summary>
public delegate uint AchievementsMemoryReader(uint address, Span<byte> buffer);

/// <summary>
/// One rcheevos <c>rc_client</c> (SPEC §7.10): sign-in, game loading, per-frame processing and events.
/// Frame calls (<see cref="DoFrame"/>, <see cref="Idle"/>, <see cref="Reset"/>, progress) belong on the
/// emulation thread; sign-in and loading can come from any thread. <see cref="Event"/> is raised on whichever
/// thread rcheevos reports it from (the emulation thread for frame events); callers marshal it.
/// </summary>
public sealed unsafe class AchievementsClient : IDisposable
{
    private readonly IAchievementsServer _server;
    private readonly TimeProvider _clock;
    private readonly long _start;
    private GCHandle _self;
    private nint _client;

    public AchievementsClient(IAchievementsServer server, TimeProvider? clock = null)
    {
        _server = server;
        _clock = clock ?? TimeProvider.System;
        _start = _clock.GetTimestamp();
        _client = Create(&ReadMemoryCallback, &ServerCallCallback);
        if (_client == 0)
        {
            throw new AchievementsException("RetroAchievements couldn't start.");
        }

        _self = GCHandle.Alloc(this);
        SetUserdata(_client, GCHandle.ToIntPtr(_self));
        SetEventHandler(_client, &EventCallback);
        SetGetTimeMillisecsFunction(_client, &TimeCallback);
        SetAllowBackgroundMemoryReads(_client, 0);
        EnableLogging(_client, Environment.GetEnvironmentVariable("GAMEVAULT_RC_VERBOSE") == "1" ? 4 : LogLevelWarn, &LogCallback);
    }

    /// <summary>True when the native library can be loaded on this machine.</summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                return NativeLibrary.TryLoad(RcheevosNative.Library, typeof(AchievementsClient).Assembly, null, out _);
            }
            catch (Exception ex) when (ex is BadImageFormatException or ArgumentException)
            {
                return false;
            }
        }
    }

    /// <summary>Reads memory for rcheevos. Null reads nothing (no game running).</summary>
    public AchievementsMemoryReader? ReadMemory { get; set; }

    public event EventHandler<AchievementsEvent>? Event;

    /// <summary>rcheevos' warnings and errors, for the log.</summary>
    public event EventHandler<string>? Log;

    /// <summary>rcheevos' part of the HTTP User-Agent, for example "rcheevos/12.5".</summary>
    public string UserAgentClause
    {
        get
        {
            var buffer = stackalloc byte[128];
            var length = (int)GetUserAgentClause(Handle, buffer, 128);
            return System.Text.Encoding.UTF8.GetString(buffer, Math.Clamp(length, 0, 127));
        }
    }

    public bool HardcoreEnabled
    {
        get => GetHardcoreEnabled(Handle) != 0;
        set => SetHardcoreEnabled(Handle, value ? 1 : 0);
    }

    public AchievementsUser? User
    {
        get
        {
            var user = GetUserInfo(Handle);
            return user is null
                ? null
                : new AchievementsUser(
                    Utf8(user->Username) ?? string.Empty,
                    Utf8(user->DisplayName) ?? Utf8(user->Username) ?? string.Empty,
                    user->Score,
                    user->ScoreSoftcore,
                    Utf8(user->AvatarUrl));
        }
    }

    /// <summary>The login token to keep (encrypted) instead of the password.</summary>
    public string? Token
    {
        get
        {
            var user = GetUserInfo(Handle);
            return user is null ? null : Utf8(user->Token);
        }
    }

    public bool IsGameLoaded => IsGameLoaded(Handle) != 0;

    public AchievementsGame? Game
    {
        get
        {
            if (!IsGameLoaded)
            {
                return null;
            }

            var game = GetGameInfo(Handle);
            if (game is null)
            {
                return null;
            }

            var all = Achievements().SelectMany(g => g.Achievements).DistinctBy(a => a.Id).ToList();
            return new AchievementsGame(game->Id, Utf8(game->Title) ?? string.Empty, Utf8(game->BadgeUrl), all.Count, all.Count(a => a.Unlocked));
        }
    }

    /// <summary>"Playing World 1-2, 3 lives", or null when the game has no rich presence.</summary>
    public string? RichPresence
    {
        get
        {
            var buffer = stackalloc byte[256];
            var length = (int)GetRichPresenceMessage(Handle, buffer, 256);
            return length <= 0 ? null : System.Text.Encoding.UTF8.GetString(buffer, Math.Min(length, 255));
        }
    }

    private nint Handle => _client != 0 ? _client : throw new ObjectDisposedException(nameof(AchievementsClient));

    public Task LoginWithPasswordAsync(string userName, string password)
    {
        var (task, userdata) = NewCall();
        BeginLoginWithPassword(Handle, userName, password, &CompletionCallback, userdata);
        return task;
    }

    public Task LoginWithTokenAsync(string userName, string token)
    {
        var (task, userdata) = NewCall();
        BeginLoginWithToken(Handle, userName, token, &CompletionCallback, userdata);
        return task;
    }

    public void Logout() => RcheevosNative.Logout(Handle);

    /// <summary>Identifies the game by its RetroAchievements hash and loads its achievements.</summary>
    public Task LoadGameAsync(string hash)
    {
        var (task, userdata) = NewCall();
        BeginLoadGame(Handle, hash, &CompletionCallback, userdata);
        return task;
    }

    public void UnloadGame() => RcheevosNative.UnloadGame(Handle);

    /// <summary>After every emulated frame (emulation thread).</summary>
    public void DoFrame() => RcheevosNative.DoFrame(Handle);

    /// <summary>While paused, so pending requests and pings still go out.</summary>
    public void Idle() => RcheevosNative.Idle(Handle);

    /// <summary>The console was reset.</summary>
    public void Reset() => RcheevosNative.Reset(Handle);

    /// <summary>Achievement progress for a save state.</summary>
    public byte[] SerializeProgress()
    {
        var size = (int)ProgressSize(Handle);
        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            return RcheevosNative.SerializeProgress(Handle, p, (nuint)size) == Ok ? buffer : [];
        }
    }

    /// <summary>Restores progress from a save state; an empty buffer resets it.</summary>
    public bool DeserializeProgress(ReadOnlySpan<byte> progress)
    {
        fixed (byte* p = progress)
        {
            return RcheevosNative.DeserializeProgress(Handle, progress.IsEmpty ? null : p, (nuint)progress.Length) == Ok;
        }
    }

    /// <summary>The game's achievements grouped as rcheevos groups them for display.</summary>
    public IReadOnlyList<AchievementGroup> Achievements()
    {
        if (!IsGameLoaded)
        {
            return [];
        }

        var list = CreateAchievementList(Handle, CategoryCore, ListGroupingProgress);
        if (list is null)
        {
            return [];
        }

        try
        {
            var groups = new List<AchievementGroup>((int)list->NumBuckets);
            for (var b = 0; b < list->NumBuckets; b++)
            {
                var bucket = &list->Buckets[b];
                var items = new List<AchievementInfo>((int)bucket->NumAchievements);
                for (var i = 0; i < bucket->NumAchievements; i++)
                {
                    items.Add(ToInfo(bucket->Achievements[i]));
                }

                groups.Add(new AchievementGroup(Utf8(bucket->Label) ?? string.Empty, items));
            }

            return groups;
        }
        finally
        {
            DestroyAchievementList(list);
        }
    }

    public void Dispose()
    {
        if (_client != 0)
        {
            RcheevosNative.Destroy(_client);
            _client = 0;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    // ---------------------------------------------------------------- async calls

    private static (Task Task, nint Userdata) NewCall()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return (completion.Task, GCHandle.ToIntPtr(GCHandle.Alloc(completion)));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CompletionCallback(int result, byte* errorMessage, nint client, nint userdata)
    {
        var handle = GCHandle.FromIntPtr(userdata);
        var completion = (TaskCompletionSource)handle.Target!;
        handle.Free();
        if (result == Ok)
        {
            completion.TrySetResult();
        }
        else
        {
            completion.TrySetException(new AchievementsException(Describe(result, Utf8(errorMessage)), result));
        }
    }

    private static string Describe(int result, string? message) => result switch
    {
        InvalidCredentials => "RetroAchievements didn't accept that username and password.",
        ExpiredToken => "Your RetroAchievements sign-in has expired. Please sign in again.",
        NoResponse => "RetroAchievements didn't answer. Check the internet connection and try again.",
        LoginRequired => "Sign in to RetroAchievements first.",
        _ when !string.IsNullOrWhiteSpace(message) => message,
        _ => $"RetroAchievements reported an error ({result}).",
    };

    // ---------------------------------------------------------------- native callbacks

    private static AchievementsClient? From(nint client)
    {
        var userdata = client == 0 ? 0 : GetUserdata(client);
        return userdata == 0 ? null : GCHandle.FromIntPtr(userdata).Target as AchievementsClient;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint ReadMemoryCallback(uint address, byte* buffer, uint numBytes, nint client)
    {
        try
        {
            return From(client)?.ReadMemory is { } read ? read(address, new Span<byte>(buffer, (int)numBytes)) : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong TimeCallback(nint client) =>
        From(client) is { } self ? (ulong)self._clock.GetElapsedTime(self._start).TotalMilliseconds : 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void LogCallback(byte* message, nint client)
    {
        try
        {
            if (From(client) is { } self && Utf8(message) is { } text)
            {
                self.Log?.Invoke(self, text);
            }
        }
        catch (Exception)
        {
            // Logging never breaks rcheevos.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ServerCallCallback(ApiRequest* request, nint callback, nint callbackData, nint client)
    {
        var self = From(client);
        var url = Utf8(request->Url) ?? string.Empty;
        var postData = Utf8(request->PostData);
        var contentType = Utf8(request->ContentType);
        if (self is null)
        {
            Respond(callback, callbackData, new AchievementsServerResponse(ServerResponseClientError, []));
            return;
        }

        Task<AchievementsServerResponse> task;
        try
        {
            task = self._server.PostAsync(url, postData, contentType);
        }
        catch (Exception)
        {
            task = Task.FromResult(new AchievementsServerResponse(ServerResponseRetryableClientError, []));
        }

        if (task.IsCompleted)
        {
            Respond(callback, callbackData, Result(task));
        }
        else
        {
            task.ContinueWith(t => Respond(callback, callbackData, Result(t)), TaskScheduler.Default);
        }
    }

    private static AchievementsServerResponse Result(Task<AchievementsServerResponse> task) =>
        task.IsCompletedSuccessfully ? task.Result : new AchievementsServerResponse(ServerResponseRetryableClientError, []);

    private static void Respond(nint callback, nint callbackData, AchievementsServerResponse response)
    {
        // rcheevos parses the body inside the callback, so it only needs to live for the call.
        fixed (byte* body = response.Body.Length == 0 ? [0] : response.Body)
        {
            var native = new ServerResponse { Body = body, BodyLength = (nuint)response.Body.Length, HttpStatusCode = response.StatusCode };
            ((delegate* unmanaged[Cdecl]<ServerResponse*, nint, void>)callback)(&native, callbackData);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void EventCallback(Event* e, nint client)
    {
        try
        {
            if (From(client) is { } self && ToEvent(e) is { } managed)
            {
                self.Event?.Invoke(self, managed);
            }
        }
        catch (Exception ex)
        {
            if (From(client) is { } self)
            {
                self.Log?.Invoke(self, "Event handler failed: " + ex.Message);
            }
        }
    }

    private static AchievementsEvent? ToEvent(Event* e)
    {
        var achievement = e->Achievement is null ? null : ToInfo(e->Achievement);
        var leaderboard = e->Leaderboard is null
            ? null
            : new LeaderboardInfo(e->Leaderboard->Id, Utf8(e->Leaderboard->Title) ?? string.Empty, Utf8(e->Leaderboard->Description) ?? string.Empty);
        var trackerId = e->LeaderboardTracker is null ? 0 : e->LeaderboardTracker->Id;
        var trackerText = e->LeaderboardTracker is null ? null : Utf8(e->LeaderboardTracker->Display, 24);
        var message = e->ServerError is null ? null : Utf8(e->ServerError->ErrorMessage);
        if (e->Type is < 1 or > 19)
        {
            return null;
        }

        return new AchievementsEvent((AchievementsEventKind)(e->Type - 1), achievement, leaderboard, trackerId, trackerText, message);
    }

    private static AchievementInfo ToInfo(Achievement* a) => new(
        a->Id,
        Utf8(a->Title) ?? string.Empty,
        Utf8(a->Description) ?? string.Empty,
        a->Points,
        Utf8(a->Unlocked != 0 ? a->BadgeUrl : a->BadgeLockedUrl) ?? Utf8(a->BadgeUrl),
        a->Unlocked != 0,
        Utf8(a->MeasuredProgress, 24),
        a->MeasuredPercent);
}
