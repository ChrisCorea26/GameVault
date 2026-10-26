using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GameVault.Platform.Achievements;

/// <summary>
/// P/Invoke declarations for rcheevos' <c>rc_client</c> API (native/rcheevos, v12.5.0, MIT). Struct layouts
/// mirror <c>include/rc_client.h</c> and <c>include/rc_api_request.h</c>; only the functions and fields
/// GameVault uses are declared. All strings are UTF-8.
/// </summary>
internal static unsafe partial class RcheevosNative
{
    public const string Library = "rcheevos";

    // ---------------------------------------------------------------- result codes (rc_error.h)
    public const int Ok = 0;
    public const int LoginRequired = -28;
    public const int NoGameLoaded = -29;
    public const int NoResponse = -32;
    public const int InvalidCredentials = -34;
    public const int ExpiredToken = -35;

    // rc_api_server_response_t.http_status_code for transport failures
    public const int ServerResponseClientError = -1;
    public const int ServerResponseRetryableClientError = -2;

    // ---------------------------------------------------------------- enums used by GameVault
    public const int LogLevelWarn = 2;
    public const int CategoryCore = 1;
    public const int ListGroupingProgress = 1;

    // ---------------------------------------------------------------- structs

    [StructLayout(LayoutKind.Sequential)]
    public struct ApiRequest
    {
        public byte* Url;
        public byte* PostData;
        public byte* ContentType;
        // rc_buffer_t follows; never read.
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ServerResponse
    {
        public byte* Body;
        public nuint BodyLength;
        public int HttpStatusCode;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct User
    {
        public byte* DisplayName;
        public byte* Username;
        public byte* Token;
        public uint Score;
        public uint ScoreSoftcore;
        public uint NumUnreadMessages;
        public byte* AvatarUrl;
        public long AvatarLastUpdated;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Game
    {
        public uint Id;
        public uint ConsoleId;
        public byte* Title;
        public byte* Hash;
        public byte* BadgeName;
        public byte* BadgeUrl;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Achievement
    {
        public byte* Title;
        public byte* Description;
        public fixed byte BadgeName[8];
        public fixed byte MeasuredProgress[24];
        public float MeasuredPercent;
        public uint Id;
        public uint Points;
        public long UnlockTime;
        public byte State;
        public byte Category;
        public byte Bucket;
        public byte Unlocked;
        public float Rarity;
        public float RarityHardcore;
        public byte Type;
        public byte* BadgeUrl;
        public byte* BadgeLockedUrl;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AchievementBucket
    {
        public Achievement** Achievements;
        public uint NumAchievements;
        public byte* Label;
        public uint SubsetId;
        public byte BucketType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AchievementList
    {
        public AchievementBucket* Buckets;
        public uint NumBuckets;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Leaderboard
    {
        public byte* Title;
        public byte* Description;
        public byte* TrackerValue;
        public uint Id;
        public byte State;
        public byte Format;
        public byte LowerIsBetter;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LeaderboardTracker
    {
        public fixed byte Display[24];
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ServerError
    {
        public byte* ErrorMessage;
        public byte* Api;
        public int Result;
        public uint RelatedId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Event
    {
        public uint Type;
        public Achievement* Achievement;
        public Leaderboard* Leaderboard;
        public LeaderboardTracker* LeaderboardTracker;
        public void* LeaderboardScoreboard;
        public ServerError* ServerError;
        public void* Subset;
    }

    // ---------------------------------------------------------------- functions

    [LibraryImport(Library, EntryPoint = "rc_client_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint Create(
        delegate* unmanaged[Cdecl]<uint, byte*, uint, nint, uint> readMemory,
        delegate* unmanaged[Cdecl]<ApiRequest*, nint, nint, nint, void> serverCall);

    [LibraryImport(Library, EntryPoint = "rc_client_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void Destroy(nint client);

    [LibraryImport(Library, EntryPoint = "rc_client_set_userdata")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetUserdata(nint client, nint userdata);

    [LibraryImport(Library, EntryPoint = "rc_client_get_userdata")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint GetUserdata(nint client);

    [LibraryImport(Library, EntryPoint = "rc_client_set_event_handler")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetEventHandler(nint client, delegate* unmanaged[Cdecl]<Event*, nint, void> handler);

    [LibraryImport(Library, EntryPoint = "rc_client_set_get_time_millisecs_function")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetGetTimeMillisecsFunction(nint client, delegate* unmanaged[Cdecl]<nint, ulong> handler);

    [LibraryImport(Library, EntryPoint = "rc_client_set_allow_background_memory_reads")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetAllowBackgroundMemoryReads(nint client, int allowed);

    [LibraryImport(Library, EntryPoint = "rc_client_enable_logging")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void EnableLogging(nint client, int level, delegate* unmanaged[Cdecl]<byte*, nint, void> callback);

    [LibraryImport(Library, EntryPoint = "rc_client_set_host")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetHost(nint client, [MarshalAs(UnmanagedType.LPUTF8Str)] string? hostname);

    [LibraryImport(Library, EntryPoint = "rc_client_get_user_agent_clause")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nuint GetUserAgentClause(nint client, byte* buffer, nuint bufferSize);

    [LibraryImport(Library, EntryPoint = "rc_client_set_hardcore_enabled")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetHardcoreEnabled(nint client, int enabled);

    [LibraryImport(Library, EntryPoint = "rc_client_get_hardcore_enabled")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int GetHardcoreEnabled(nint client);

    [LibraryImport(Library, EntryPoint = "rc_client_begin_login_with_password")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint BeginLoginWithPassword(
        nint client,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string username,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string password,
        delegate* unmanaged[Cdecl]<int, byte*, nint, nint, void> callback,
        nint userdata);

    [LibraryImport(Library, EntryPoint = "rc_client_begin_login_with_token")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint BeginLoginWithToken(
        nint client,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string username,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string token,
        delegate* unmanaged[Cdecl]<int, byte*, nint, nint, void> callback,
        nint userdata);

    [LibraryImport(Library, EntryPoint = "rc_client_logout")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void Logout(nint client);

    [LibraryImport(Library, EntryPoint = "rc_client_get_user_info")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial User* GetUserInfo(nint client);

    [LibraryImport(Library, EntryPoint = "rc_client_begin_load_game")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint BeginLoadGame(
        nint client,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string hash,
        delegate* unmanaged[Cdecl]<int, byte*, nint, nint, void> callback,
        nint userdata);

    [LibraryImport(Library, EntryPoint = "rc_client_is_game_loaded")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int IsGameLoaded(nint client);

    [LibraryImport(Library, EntryPoint = "rc_client_unload_game")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void UnloadGame(nint client);

    [LibraryImport(Library, EntryPoint = "rc_client_get_game_info")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial Game* GetGameInfo(nint client);

    [LibraryImport(Library, EntryPoint = "rc_client_create_achievement_list")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial AchievementList* CreateAchievementList(nint client, int category, int grouping);

    [LibraryImport(Library, EntryPoint = "rc_client_destroy_achievement_list")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void DestroyAchievementList(AchievementList* list);

    [LibraryImport(Library, EntryPoint = "rc_client_get_rich_presence_message")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nuint GetRichPresenceMessage(nint client, byte* buffer, nuint bufferSize);

    [LibraryImport(Library, EntryPoint = "rc_client_do_frame")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void DoFrame(nint client);

    [LibraryImport(Library, EntryPoint = "rc_client_idle")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void Idle(nint client);

    [LibraryImport(Library, EntryPoint = "rc_client_reset")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void Reset(nint client);

    [LibraryImport(Library, EntryPoint = "rc_client_progress_size")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nuint ProgressSize(nint client);

    [LibraryImport(Library, EntryPoint = "rc_client_serialize_progress_sized")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int SerializeProgress(nint client, byte* buffer, nuint bufferSize);

    [LibraryImport(Library, EntryPoint = "rc_client_deserialize_progress_sized")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int DeserializeProgress(nint client, byte* serialized, nuint serializedSize);

    public static string? Utf8(byte* text) => text is null ? null : Marshal.PtrToStringUTF8((nint)text);

    public static string Utf8(byte* text, int maxLength)
    {
        var length = 0;
        while (length < maxLength && text[length] != 0)
        {
            length++;
        }

        return System.Text.Encoding.UTF8.GetString(text, length);
    }
}
