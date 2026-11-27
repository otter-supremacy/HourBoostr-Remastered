using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Xml;
using SteamKit2;
using SteamKit2.Internal;

namespace HourBoostr.Remastered;

internal sealed class SteamGroupJoin(
    SteamClient client, SteamFriends friends, ChatRoom chat,
    Func<string?> refreshToken, Func<string, bool> connected, Action refreshSubscriptions)
{
    private static readonly HttpClient community = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(7),
        MaxResponseContentBufferSize = 1024 * 1024
    };
    private string? webToken;
    private DateTimeOffset webTokenUntil;
    private string? webTokenOwner;

    public async Task<ResolvedGroup> Resolve(string owner, GroupInvitation invite, CancellationToken token)
    {
        if (!invite.IsValidTarget()) throw new GroupJoinException("Unsupported Steam group target");
        RequireConnection(owner);
        if (invite.Kind is GroupKind.ChatLink or GroupKind.ChatInvite)
            return await ResolveChat(owner, invite, token);
        var clan = invite.Kind == GroupKind.CommunityName
            ? await ResolveVanity(invite.Value, token)
            : ulong.Parse(invite.Value, CultureInfo.InvariantCulture);
        var steamId = new SteamID(clan);
        var relationship = friends.GetClanRelationship(steamId);
        var key = new GroupInvitation(GroupKind.CommunityId, clan.ToString(CultureInfo.InvariantCulture)).Key();
        if (relationship is EClanRelationship.PendingApproval)
            throw new GroupJoinException("Pending Steam group approval");
        if (relationship is EClanRelationship.Blocked or EClanRelationship.Kicked or EClanRelationship.RequestDenied)
            throw new GroupJoinException("Steam group is restricted for this account");
        if (invite.Kind == GroupKind.ClanInvite && relationship is not (EClanRelationship.Invited or EClanRelationship.Member))
            throw new GroupJoinException("Steam group invitation is no longer available");
        return new(key, relationship == EClanRelationship.Member, cancellation => JoinClan(owner, steamId, cancellation));
    }

    private void RequireConnection(string owner)
    {
        if (!connected(owner)) throw new GroupJoinException("Steam connection changed");
    }

    private static async Task<ulong> ResolveVanity(string name, CancellationToken token)
    {
        using var result = await community.GetAsync($"https://steamcommunity.com/groups/{name}/memberslistxml/?xml=1", token);
        if (result.StatusCode == HttpStatusCode.TooManyRequests)
            throw new GroupJoinException("Steam rate limit", true);
        if (!result.IsSuccessStatusCode) throw new GroupJoinException("Steam group is unavailable");
        await using var stream = await result.Content.ReadAsStreamAsync(token);
        using var xml = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 1024 * 1024 });
        xml.MoveToContent();
        if (xml.Name == "memberList" && xml.ReadToDescendant("groupID64")
            && ulong.TryParse(xml.ReadElementContentAsString(), out var clan) && GroupInvitation.IsClan(clan)) return clan;
        throw new GroupJoinException("Steam did not identify this Community group");
    }

    private async Task<string> JoinClan(string owner, SteamID clan, CancellationToken token)
    {
        RequireConnection(owner);
        switch (friends.GetClanRelationship(clan))
        {
            case EClanRelationship.Member: return "Already a member";
            case EClanRelationship.PendingApproval: return "Pending Steam group approval";
            case EClanRelationship.Blocked:
            case EClanRelationship.Kicked:
            case EClanRelationship.RequestDenied: throw new GroupJoinException("Steam group is restricted for this account");
            case EClanRelationship.Invited:
                var message = new ClientMsg<ClanInviteAcknowledgement>();
                message.Body.Clan = clan.ConvertToUInt64();
                client.Send(message);
                break;
            default:
                await JoinCommunityWeb(owner, clan, token);
                break;
        }
        for (var i = 0; i < 12; i++)
        {
            RequireConnection(owner);
            var status = friends.GetClanRelationship(clan);
            if (status == EClanRelationship.Member) return "Joined Steam Community group";
            if (status == EClanRelationship.PendingApproval) return "Pending Steam group approval";
            if (status is EClanRelationship.Blocked or EClanRelationship.RequestDenied or EClanRelationship.Kicked)
                throw new GroupJoinException("Steam declined the group join");
            await Task.Delay(TimeSpan.FromMilliseconds(400), token);
        }
        return "Join submitted, membership unconfirmed";
    }

    private async Task JoinCommunityWeb(string owner, SteamID clan, CancellationToken token)
    {
        if (webToken is null || webTokenOwner != owner || webTokenUntil <= DateTimeOffset.UtcNow)
        {
            var refresh = refreshToken();
            if (string.IsNullOrEmpty(refresh)) throw new GroupJoinException("Saved Steam session is required for Community links");
            var access = await client.Authentication.GenerateAccessTokenForAppAsync(client.SteamID!, refresh, false)
                .WaitAsync(TimeSpan.FromSeconds(7), token);
            webToken = access.AccessToken;
            webTokenOwner = owner;
            webTokenUntil = DateTimeOffset.UtcNow.AddMinutes(10);
        }
        RequireConnection(owner);
        var sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var url = new Uri($"https://steamcommunity.com/gid/{clan.ConvertToUInt64()}");
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Referrer = url;
        request.Headers.Add("Origin", "https://steamcommunity.com");
        request.Headers.Add("Cookie", $"sessionid={sessionId}; steamLoginSecure={Uri.EscapeDataString($"{owner}||{webToken}")}");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["action"] = "join", ["sessionID"] = sessionId });
        // Never forward the Steam cookie or repeat the POST on a redirect.
        using var result = await community.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (result.StatusCode == HttpStatusCode.TooManyRequests) throw new GroupJoinException("Steam rate limit", true);
        if (result.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            || result.Headers.Location?.OriginalString.Contains("/login", StringComparison.OrdinalIgnoreCase) == true)
        {
            webToken = null;
            throw new GroupJoinException("Steam declined the Community session or group access");
        }
        if (!result.IsSuccessStatusCode && (int)result.StatusCode is not (301 or 302 or 303))
            throw new GroupJoinException("Steam declined the Community join request");
    }

    private async Task<ResolvedGroup> ResolveChat(string owner, GroupInvitation invite, CancellationToken token)
    {
        var code = invite.Kind == GroupKind.ChatLink ? invite.Value : null;
        ulong groupId;
        var chatId = invite.ChatId;
        if (code is not null)
        {
            var info = await ReadInvite(code).WaitAsync(TimeSpan.FromSeconds(7), token);
            CheckResult(info.Result);
            if (info.Body.banned || info.Body.time_kick_expire > DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                || (info.Body.time_expires != 0 && info.Body.time_expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
                throw new GroupJoinException("Chat invitation is expired or restricted");
            groupId = info.Body.group_summary?.chat_group_id ?? 0;
            chatId = info.Body.chat_id;
        }
        else
        {
            groupId = ulong.Parse(invite.Value, CultureInfo.InvariantCulture);
        }
        if (groupId == 0) throw new GroupJoinException("Steam did not identify the chat group");
        var joined = await ChatMembership(groupId, token);
        if (!joined && code is null)
            await ValidateDirectChatInvite(owner, groupId, chatId, token);
        return new(new GroupInvitation(GroupKind.ChatInvite, groupId.ToString(CultureInfo.InvariantCulture)).Key(),
            joined, cancellation => JoinChat(owner, groupId, chatId, code, cancellation));
    }

    private async Task ValidateDirectChatInvite(string owner, ulong groupId, ulong chatId, CancellationToken token)
    {
        RequireConnection(owner);
        async Task<SteamUnifiedMessages.ServiceMethodResponse<CChatRoom_GetInviteInfo_Response>> Query() =>
            await chat.GetInviteInfo(new CChatRoom_GetInviteInfo_Request {
                steamid_invitee = ulong.Parse(owner, CultureInfo.InvariantCulture),
                chat_group_id = groupId, chat_id = chatId
            });
        var info = await Query().WaitAsync(TimeSpan.FromSeconds(5), token);
        CheckResult(info.Result);
        if (info.Body.group_summary?.chat_group_id != groupId || info.Body.banned
            || info.Body.time_kick_expire > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            throw new GroupJoinException("Steam did not confirm an eligible direct chat invitation");
    }

    private async Task<SteamUnifiedMessages.ServiceMethodResponse<CChatRoom_GetInviteLinkInfo_Response>> ReadInvite(string code) =>
        await chat.GetInviteLinkInfo(new CChatRoom_GetInviteLinkInfo_Request { invite_code = code });

    private async Task<bool> ChatMembership(ulong id, CancellationToken token)
    {
        async Task<SteamUnifiedMessages.ServiceMethodResponse<CChatRoom_GetMyChatRoomGroups_Response>> Query() =>
            await chat.GetMyChatRoomGroups(new CChatRoom_GetMyChatRoomGroups_Request());
        var list = await Query().WaitAsync(TimeSpan.FromSeconds(6), token);
        CheckResult(list.Result);
        return list.Body.chat_room_groups.Any(group => group.group_summary?.chat_group_id == id
            && group.user_chat_group_state is { time_joined: > 0 });
    }

    private async Task<string> JoinChat(string owner, ulong id, ulong chatId, string? code, CancellationToken token)
    {
        RequireConnection(owner);
        async Task<SteamUnifiedMessages.ServiceMethodResponse<CChatRoom_JoinChatRoomGroup_Response>> Join() =>
            await chat.JoinChatRoomGroup(new CChatRoom_JoinChatRoomGroup_Request { chat_group_id = id, chat_id = chatId, invite_code = code ?? "" });
        var result = await Join().WaitAsync(TimeSpan.FromSeconds(7), token);
        CheckResult(result.Result);
        var confirmed = result.Body.user_chat_state is { time_joined: > 0 } state && state.chat_group_id == id
            || await ChatMembership(id, token);
        if (!confirmed) return "Join submitted, membership unconfirmed";
        refreshSubscriptions();
        return "Joined Steam group chat";
    }

    private static void CheckResult(EResult result)
    {
        if (result == EResult.OK) return;
        if (result is EResult.RateLimitExceeded or EResult.LimitExceeded)
            throw new GroupJoinException("Steam rate or membership limit", true);
        throw new GroupJoinException($"Steam declined or could not confirm access ({result})");
    }
}

internal sealed class ClanInviteAcknowledgement : ISteamSerializableMessage
{
    public ulong Clan { get; set; }
    public EMsg GetEMsg() => EMsg.ClientAcknowledgeClanInvite;
    public void Serialize(Stream stream)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(Clan);
        writer.Write((byte)1);
    }

    public void Deserialize(Stream stream) => throw new NotSupportedException();
}
