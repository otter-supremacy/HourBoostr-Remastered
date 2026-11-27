using System.Text;
using HourBoostr.Remastered;

namespace HourBoostr.Members;

internal sealed record MemberAppearance(string Status = "online", string Badge = "normal", string CustomText = "")
{
    public string? Validate()
    {
        if (!SteamAppearance.IsPresence(Status) || !SteamAppearance.IsBadge(Badge)) return "Choose a supported status and display badge.";
        if (CustomText is null || CustomText.Length > 80 || Encoding.UTF8.GetByteCount(CustomText) > 128 || CustomText.Any(char.IsControl))
            return "Custom text must fit on one line, up to 80 characters and 128 UTF-8 bytes.";
        return null;
    }
}
