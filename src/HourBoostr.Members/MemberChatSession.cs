using System.Diagnostics;
using SteamKit2;
using SteamKit2.Internal;
using SteamKit2.WebUI.Internal;

namespace HourBoostr.Members;

internal sealed class MemberChatSession
{
    private readonly SteamClient client;
    private readonly SteamFriends friends;
    private readonly FriendMessages messages;
    private readonly ChatRoom rooms;
    private readonly MemberStore store;
    private readonly MemberChatAi ai;
    private readonly Work work;
    private readonly CancellationToken stop;
    private readonly object gate = new();
    private readonly Dictionary<ulong, DateTimeOffset> dmCooldown = [];
    private readonly Dictionary<(ulong Group, ulong Room), (int Next, DateTimeOffset NextAt)> rotation = [];
    private readonly HashSet<(ulong Group, ulong Room, uint At, uint Ordinal)> seenGroup = [];
    private readonly Queue<(ulong Group, ulong Room, uint At, uint Ordinal)> groupOrder = [];
    private readonly HashSet<ulong> pending = [];
    private readonly Dictionary<ulong, DateTimeOffset> acceptAttempts = [];
    private readonly HashSet<ulong> awaitingConfirmation = [];
    private readonly Queue<DateTimeOffset> outbound = new();
    private readonly Queue<(string Target, string Text)> recent = new();
    private IReadOnlySet<ulong> joined = new HashSet<ulong>();
    private ChatGroup[] availableGroups = [];
    public ChatGroup[] Groups { get { lock (gate) return availableGroups; } }
    public string? OptIn => Self == 0 ? null : $"!addme {Self}";
    private int syncing;
    private DateTimeOffset nextSync;
    private DateTimeOffset nextAccept;

    public MemberChatSession(SteamClient client, CallbackManager callbacks, MemberStore store,
        MemberChatAi ai, Work work, CancellationToken stop)
    {
        this.client = client;
        this.store = store;
        this.ai = ai;
        this.work = work;
        this.stop = stop;
        friends = client.GetHandler<SteamFriends>()!;
        var unified = client.GetHandler<SteamUnifiedMessages>()!;
        messages = unified.CreateService<FriendMessages>();
        rooms = unified.CreateService<ChatRoom>();
        callbacks.Subscribe<SteamFriends.FriendsListCallback>(UpdateFriends);
        callbacks.Subscribe<SteamFriends.FriendAddedCallback>(ConfirmAcceptance);
        callbacks.Subscribe<SteamFriends.FriendMsgCallback>(m =>
            _ = Private(m.Sender.ConvertToUInt64(), (int)m.EntryType, false, m.Message ?? ""));
        callbacks.SubscribeServiceNotification<FriendMessagesClient, CFriendMessages_IncomingMessage_Notification>(m =>
            _ = Private(m.Body.steamid_friend, m.Body.chat_entry_type, m.Body.local_echo, m.Body.message));
        callbacks.SubscribeServiceNotification<ChatRoomClient, CChatRoom_IncomingChatMessage_Notification>(m => _ = Group(m.Body));
    }

    private ulong Self => client.SteamID?.ConvertToUInt64() ?? 0;
    private bool Active => !stop.IsCancellationRequested && client.IsConnected && Self != 0 && store.ChatActive(work);

    private bool ReserveMessage(string target, string incoming, DateTimeOffset now)
    {
        while (outbound.TryPeek(out var first) && now - first >= TimeSpan.FromHours(1)) outbound.Dequeue();
        if (outbound.Count >= 12 || recent.Any(x => x.Target == target && x.Text == incoming && incoming.Length > 0)) return false;
        outbound.Enqueue(now);
        return true;
    }

    private void Record(string target, string reply)
    {
        lock (gate)
        {
            recent.Enqueue((target, reply));
            if (recent.Count > 32) recent.Dequeue();
        }
    }

    public void Tick()
    {
        if (!Active) return;
        var settings = store.Chat(work.Id);
        var now = DateTimeOffset.UtcNow;
        if (now >= nextSync && Interlocked.CompareExchange(ref syncing, 1, 0) == 0)
        {
            nextSync = now.AddSeconds(30);
            _ = SubscribeGroups(settings);
        }
        if (!settings.AutoAccept || now < nextAccept) return;
        ulong candidate = 0;
        lock (gate)
        {
            foreach (var old in acceptAttempts.Where(x => x.Value < now.AddHours(-1)).Select(x => x.Key).ToArray())
            {
                acceptAttempts.Remove(old);
                awaitingConfirmation.Remove(old);
            }
            if (acceptAttempts.Count >= 12) return;
            foreach (var id in pending)
            {
                if (acceptAttempts.ContainsKey(id) || id == Self
                    || friends.GetFriendRelationship(new SteamID(id)) != EFriendRelationship.RequestRecipient) continue;
                candidate = id;
                acceptAttempts[id] = now;
                awaitingConfirmation.Add(id);
                nextAccept = now.AddSeconds(10);
                break;
            }
        }
        if (candidate == 0 || !Active || !store.Chat(work.Id).AutoAccept) return;
        try { friends.AddFriend(new SteamID(candidate)); }
        catch (InvalidOperationException) { lock (gate) awaitingConfirmation.Remove(candidate); }
    }

    private async Task SubscribeGroups(MemberChatSettings settings)
    {
        try
        {
            var request = new CChatRoom_SetSessionActiveChatRoomGroups_Request();
            var response = await rooms.GetMyChatRoomGroups(new CChatRoom_GetMyChatRoomGroups_Request())
                .ToTask().WaitAsync(TimeSpan.FromSeconds(12), stop);
            if (response.Result != EResult.OK || !Active) return;
            var groups = response.Body.chat_room_groups.Where(g => g.group_summary is not null)
                .Select(g => new ChatGroup(g.group_summary.chat_group_id.ToString(), g.group_summary.chat_group_name ?? "Steam group"))
                .DistinctBy(g => g.Id).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            request.chat_group_ids.AddRange(groups.Where(g => settings.GroupReplies || settings.RegularGroups.Contains(g.Id))
                .Select(g => ulong.Parse(g.Id, System.Globalization.CultureInfo.InvariantCulture)));
            var updated = await rooms.SetSessionActiveChatRoomGroups(request).ToTask().WaitAsync(TimeSpan.FromSeconds(12), stop);
            if (updated.Result != EResult.OK) return;
            lock (gate)
            {
                joined = request.chat_group_ids.ToHashSet();
                availableGroups = groups;
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or TimeoutException or InvalidOperationException) { }
        finally { Volatile.Write(ref syncing, 0); }
    }

    private void UpdateFriends(SteamFriends.FriendsListCallback update)
    {
        lock (gate)
        {
            if (!update.Incremental) pending.Clear();
            foreach (var friend in update.FriendList)
            {
                var id = friend.SteamID.ConvertToUInt64();
                if (friend.SteamID.AccountType == EAccountType.Individual && friend.Relationship == EFriendRelationship.RequestRecipient)
                {
                    if (pending.Count < 256) pending.Add(id);
                }
                else pending.Remove(id);
            }
        }
    }

    private void ConfirmAcceptance(SteamFriends.FriendAddedCallback confirmation)
    {
        var id = confirmation.SteamID.ConvertToUInt64();
        lock (gate)
        {
            if (!awaitingConfirmation.Remove(id) || confirmation.Result != EResult.OK) return;
            pending.Remove(id);
        }
        _ = Welcome(id);
    }

    private async Task Welcome(ulong id)
    {
        try
        {
            if (!Active) return;
            var settings = store.Chat(work.Id);
            if (!settings.AutoAccept || !settings.WelcomeEnabled) return;
            var target = "friend:" + id;
            lock (gate) if (!ReserveMessage(target, "", DateTimeOffset.UtcNow)) return;
            await Wait();
            settings = store.Chat(work.Id);
            if (Active && settings.AutoAccept && settings.WelcomeEnabled)
                await SendPrivate(id, target, settings.WelcomeMessage);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or TimeoutException or InvalidOperationException) { }
    }

    private async Task Private(ulong sender, int kind, bool echo, string incoming)
    {
        try
        {
            if (!Active || echo || sender == Self || sender == 0 || kind != (int)EChatEntryType.ChatMsg
                || string.IsNullOrWhiteSpace(incoming) || store.IsChatMember(sender)
                || friends.GetFriendRelationship(new SteamID(sender)) != EFriendRelationship.Friend) return;
            var settings = store.Chat(work.Id);
            var now = DateTimeOffset.UtcNow;
            var target = "friend:" + sender;
            if (!settings.AllowsPrivate(sender, now)) return;
            lock (gate)
            {
                if (dmCooldown.TryGetValue(sender, out var last) && now - last < TimeSpan.FromMinutes(settings.PrivateCooldownMinutes)) return;
                if (!ReserveMessage(target, incoming, now)) return;
                if (dmCooldown.Count >= 1024 && !dmCooldown.ContainsKey(sender))
                    dmCooldown.Remove(dmCooldown.MinBy(x => x.Value).Key);
                dmCooldown[sender] = now;
            }
            await Wait();
            settings = store.Chat(work.Id);
            if (!Active || !settings.AllowsPrivate(sender, DateTimeOffset.UtcNow)) return;
            var reply = settings.AiPrivate ? await ai.Generate(work.Id, incoming, settings, stop) : settings.PrivateMessage;
            if (!Active || !store.Chat(work.Id).AllowsPrivate(sender, DateTimeOffset.UtcNow)
                || friends.GetFriendRelationship(new SteamID(sender)) != EFriendRelationship.Friend) return;
            if (reply is not null) await SendPrivate(sender, target, reply);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or TimeoutException or InvalidOperationException) { }
    }

    private async Task Group(CChatRoom_IncomingChatMessage_Notification incoming)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (!Active || incoming.server_message is not null || string.IsNullOrWhiteSpace(incoming.message)
                || incoming.steamid_sender == Self || incoming.steamid_sender == 0 || incoming.chat_id == 0
                || Math.Abs(now.ToUnixTimeSeconds() - incoming.timestamp) > 60 || store.IsChatMember(incoming.steamid_sender)) return;
            var settings = store.Chat(work.Id);
            var groupId = incoming.chat_group_id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (settings.RegularGroups.Contains(groupId) && IsEligibleRegular(incoming.steamid_sender))
            {
                var opted = string.Equals(incoming.message.Trim(), OptIn, StringComparison.OrdinalIgnoreCase);
                store.ObserveRegular(work, groupId, incoming.steamid_sender, opted, now);
                if (opted) return;
            }
            if (!settings.GroupReplies) return;
            var key = (incoming.chat_group_id, incoming.chat_id);
            var target = $"room:{key.Item1}:{key.Item2}";
            string preset;
            lock (gate)
            {
                if (!joined.Contains(incoming.chat_group_id)) return;
                var seen = (key.Item1, key.Item2, incoming.timestamp, incoming.ordinal);
                if (!seenGroup.Add(seen)) return;
                groupOrder.Enqueue(seen);
                if (groupOrder.Count > 1024) seenGroup.Remove(groupOrder.Dequeue());
                var state = rotation.GetValueOrDefault(key);
                if (now < state.NextAt || !ReserveMessage(target, incoming.message, now)) return;
                if (rotation.Count >= 512 && !rotation.ContainsKey(key))
                    rotation.Remove(rotation.MinBy(x => x.Value.NextAt).Key);
                preset = settings.GroupMessages[state.Next];
                rotation[key] = (settings.AiGroup ? state.Next : (state.Next + 1) % 3, now.AddSeconds(settings.GroupCooldownSeconds));
            }
            await Wait();
            settings = store.Chat(work.Id);
            if (!Active || !settings.GroupReplies) return;
            var reply = settings.AiGroup ? await ai.Generate(work.Id, incoming.message, settings, stop) : preset;
            if (reply is null || !Active || !store.Chat(work.Id).GroupReplies) return;
            var sender = new SteamID(incoming.steamid_sender);
            var label = new string((friends.GetFriendPersonaName(sender) ?? "").Where(c => !char.IsControl(c) && c is not ('[' or ']' or '\\')).ToArray()).Trim();
            var addressed = FormattableString.Invariant($"[mention={sender.AccountID}]@{(label is "" or "unknown" ? "member" : label)}[/mention] {reply}");
            Record(target, addressed);
            await rooms.SendChatMessage(new CChatRoom_SendChatMessage_Request
            {
                chat_group_id = key.Item1, chat_id = key.Item2, message = addressed, echo_to_sender = false
            }).ToTask().WaitAsync(TimeSpan.FromSeconds(12), stop);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or TimeoutException or InvalidOperationException) { }
    }

    private bool IsEligibleRegular(ulong sender)
    {
        var id = new SteamID(sender);
        return sender != 0 && sender != Self && id.AccountType == EAccountType.Individual
            && friends.GetFriendRelationship(id) == EFriendRelationship.None;
    }

    public bool ApproveRegular(ulong sender)
    {
        if (!Active || !IsEligibleRegular(sender) || !store.ReserveRegularRequest(work, sender, Groups.Select(g => g.Id).ToHashSet()))
            return false;
        try { friends.AddFriend(new SteamID(sender)); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private async Task SendPrivate(ulong recipient, string target, string reply)
    {
        Record(target, reply);
        await messages.SendMessage(new CFriendMessages_SendMessage_Request
        {
            steamid = recipient, chat_entry_type = (int)EChatEntryType.ChatMsg,
            message = reply, echo_to_sender = false, contains_bbcode = false
        }).ToTask().WaitAsync(TimeSpan.FromSeconds(12), stop);
    }

    private async Task Wait()
    {
        var start = Stopwatch.GetTimestamp();
        var remaining = TimeSpan.FromSeconds(3);
        do
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds)), stop);
            remaining = TimeSpan.FromSeconds(3) - Stopwatch.GetElapsedTime(start);
        } while (remaining > TimeSpan.Zero);
    }
}
