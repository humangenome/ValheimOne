using Splatform;

namespace ValheimOne.LiveMap;

internal static class ServerUserInfo
{
    // UserInfo serializes this as "Server_0"; both parser components must be non-empty.
    private static readonly PlatformUserID UserId = new PlatformUserID("Server", "0");

    public static UserInfo Create(string displayName = "Server")
    {
        return new UserInfo
        {
            Name = displayName,
            UserId = UserId,
        };
    }
}
