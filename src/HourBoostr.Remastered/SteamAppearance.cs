using SteamKit2;
using CMsgClientChangeStatus = SteamKit2.Internal.CMsgClientChangeStatus;

namespace HourBoostr.Remastered;

public static class SteamAppearance
{
    public static bool IsPresence(string? status) => status is "online" or "busy" or "away" or "invisible";
    public static bool IsBadge(string? badge) => badge is "normal" or "play-together" or "mobile" or "big-picture" or "vr";

    public static ClientMsgProtobuf<CMsgClientChangeStatus> Packet(string status, string badge)
    {
        var state = status switch
        {
            "busy" => EPersonaState.Busy,
            "away" => EPersonaState.Away,
            "invisible" => EPersonaState.Invisible,
            _ => EPersonaState.Online
        };
        var flag = state == EPersonaState.Invisible ? (EPersonaStateFlag)0 : badge switch
        {
            "play-together" => EPersonaStateFlag.RemotePlayTogether,
            "mobile" => EPersonaStateFlag.ClientTypeMobile,
            "big-picture" => EPersonaStateFlag.ClientTypeTenfoot,
            "vr" => EPersonaStateFlag.ClientTypeVR,
            _ => (EPersonaStateFlag)0
        };
        var message = new ClientMsgProtobuf<CMsgClientChangeStatus>(EMsg.ClientChangeStatus);
        message.Body.persona_state = (uint)state;
        message.Body.persona_state_flags = (uint)flag;
        return message;
    }
}
