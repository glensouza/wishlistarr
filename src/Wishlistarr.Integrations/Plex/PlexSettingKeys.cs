using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Integrations.Plex;

internal static class PlexSettingKeys
{
    public const string ClientIdentifier = MediaServerSettings.KeyPrefix + "client_identifier";
    public const string Token = MediaServerSettings.KeyPrefix + "token";
    public const string AccountId = MediaServerSettings.KeyPrefix + "account_id";
    public const string AccountName = MediaServerSettings.KeyPrefix + "account_name";
    public const string ServerId = MediaServerSettings.KeyPrefix + "server_id";
    public const string ServerName = MediaServerSettings.KeyPrefix + "server_name";
    public const string ServerUrl = MediaServerSettings.KeyPrefix + "server_url";
    public const string ServerLocal = MediaServerSettings.KeyPrefix + "server_local";
    public const string ServerRelay = MediaServerSettings.KeyPrefix + "server_relay";
    public const string ServerToken = MediaServerSettings.KeyPrefix + "server_token";

    public static readonly string[] Server = [ServerId, ServerName, ServerUrl, ServerLocal, ServerRelay, ServerToken];

    public static readonly string[] Account = [Token, AccountId, AccountName];
}
