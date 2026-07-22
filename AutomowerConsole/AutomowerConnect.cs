namespace AutomowerConsole;

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
        await _client.AuthenticateAsync();
        _authenticated = true;
    }

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
            await _client.AuthenticateAsync();
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
