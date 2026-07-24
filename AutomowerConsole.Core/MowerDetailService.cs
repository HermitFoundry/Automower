namespace AutomowerConsole.Core;

// A specific, already-resolved mower's live detail data - status, raw JSON,
// messages, single work-area detail. Distinct from MowerService, which
// handles the mower list/resolve concern (which mower are we talking about),
// not a specific mower's live data.
public class MowerDetailService
{
    public Task<MowerData> GetMowerDetailAsync(string mowerId) => AutomowerConnect.Instance.GetMowerAsync(mowerId);

    public Task<string> GetMowerRawAsync(string mowerId) => AutomowerConnect.Instance.GetMowerRawAsync(mowerId);

    public Task<MessageItem[]> GetMessagesAsync(string mowerId) => AutomowerConnect.Instance.GetMessagesAsync(mowerId);

    public Task<WorkArea> GetWorkAreaDetailAsync(string mowerId, long workAreaId)
        => AutomowerConnect.Instance.GetWorkAreaAsync(mowerId, workAreaId);
}
