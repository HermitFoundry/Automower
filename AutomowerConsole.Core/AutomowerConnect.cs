namespace AutomowerConsole.Core;

// Facade over HusqvarnaClient that owns the auth lifecycle: callers never
// call AuthenticateAsync() themselves or think about token expiry - every
// Get* method authenticates on first use and retries once if a call fails
// with what looks like an expired token.
internal class AutomowerConnect(string appKey, string appSecret)
{
    // The shared instance services use - built from config.json lazily, on
    // first actual use, not at class load. This matters: commands that
    // never touch the API (help/config/errorcodes/current) must keep
    // working with no config.json at all, so nothing here may run just
    // because a service got constructed.
    private static AutomowerConnect? _instance;
    public static AutomowerConnect Instance => _instance ??= Create();

    private static AutomowerConnect Create()
    {
        var config = Storage.LoadConfig();
        return new AutomowerConnect(config.AppKey, config.AppSecret);
    }

    private readonly HusqvarnaClient _client = new(new HttpClient(), appKey, appSecret);
    private bool _authenticated;

    public async Task AuthenticateAsync()
    {
        if (_authenticated) return;
        await AuthenticateWithRetryAsync();
        _authenticated = true;
    }

    // This process is one of several independent clients sharing the same
    // app key/secret (the 3 'track' daemons plus AutomowerWeb - see
    // startall.sh's own staggered-start fix for the original discovery of
    // this collision). Husqvarna's auth service rejects a token request as
    // "simultaneous logins" if another one for the same client id lands too
    // close to it in time - a real, purely transient collision (confirmed
    // by a user report: reloading the dashboard moments later always
    // succeeds). A short delay and retry is the fix; a genuinely bad app
    // key/secret fails identically on every attempt and still surfaces
    // once retries are exhausted, not silently swallowed.
    private async Task AuthenticateWithRetryAsync()
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await _client.AuthenticateAsync();
                return;
            }
            catch (HttpRequestException ex) when (attempt < maxAttempts && IsSimultaneousLogins(ex))
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
    }

    private static bool IsSimultaneousLogins(Exception ex)
        => ex.Message.Contains("simultaneous.logins", StringComparison.OrdinalIgnoreCase);

    private async Task<T> WithAuthAsync<T>(Func<Task<T>> action)
    {
        await AuthenticateAsync();
        try
        {
            return await action();
        }
        catch (HttpRequestException)
        {
            // Likely an expired token on a long-running session - re-auth once and retry.
            await AuthenticateWithRetryAsync();
            _authenticated = true;
            return await action();
        }
    }

    public Task<MowerData[]> GetMowersAsync() => WithAuthAsync(_client.GetMowersAsync);

    public Task<MowerData> GetMowerAsync(string mowerId) => WithAuthAsync(() => _client.GetMowerAsync(mowerId));

    public Task<string> GetMowerRawAsync(string mowerId) => WithAuthAsync(() => _client.GetMowerRawAsync(mowerId));

    public Task<WorkArea> GetWorkAreaAsync(string mowerId, long workAreaId)
        => WithAuthAsync(() => _client.GetWorkAreaAsync(mowerId, workAreaId));

    public Task<MessageItem[]> GetMessagesAsync(string mowerId) => WithAuthAsync(() => _client.GetMessagesAsync(mowerId));
}
