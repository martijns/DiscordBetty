using Discord;
using Discord.Commands;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Betty.Bot.Extensions
{
    public class Questionair : IDisposable
    {
        private DiscordSocketClient _client;
        private ITextChannel _channel;
        private SocketGuildUser _user;
        private SocketMessage _lastmessage;
        private ManualResetEvent _event;
        private List<ulong> _sentMessages = new List<ulong>();
        private string _prefix;

        public Questionair(ICommandContext context, string prefix) : this(context.Client as DiscordSocketClient, context.Channel as ITextChannel, context.User as SocketGuildUser, prefix)
        {
        }

        public Questionair(DiscordSocketClient client, ITextChannel channel, SocketGuildUser user, string prefix)
        {
            _client = client ?? throw new ArgumentException($"{nameof(client)} is null or context is incorrect", nameof(client));
            _channel = channel ?? throw new ArgumentException($"{nameof(channel)} is null or context is incorrect", nameof(channel));
            _user = user ?? throw new ArgumentException($"{nameof(user)} is null or context is incorrect", nameof(user));
            _prefix = prefix;

            _event = new ManualResetEvent(false);
            _client.MessageReceived += HandleMessageReceived;
        }

        private Task HandleMessageReceived(SocketMessage arg)
        {
            if (arg.Channel.Id != _channel.Id)
                return Task.CompletedTask;
            if (arg.Author.Id != _user.Id)
                return Task.CompletedTask;
            _lastmessage = arg;
            _event.Set();
            return Task.CompletedTask;
        }

        private async Task<SocketMessage> AskForResponse(string question, int timeoutInSeconds = 120)
        {
            while (true)
            {
                _event.Reset();
                var sentmsg = await _channel.SendMessageAsync($"{_prefix} {question}\n*(Use `cancel` to abort)*");
                var start = DateTime.UtcNow;
                while (!_event.WaitOne(1) && start.AddSeconds(timeoutInSeconds) > DateTime.UtcNow)
                    await Task.Delay(100);
                await sentmsg.DeleteAsync();
                if (!_event.WaitOne(1))
                    throw new ApplicationException("Command timed out while waiting for an answer");
                if (_lastmessage.Content.Equals("cancel", StringComparison.InvariantCultureIgnoreCase) ||
                    _lastmessage.Content.Equals("abort", StringComparison.InvariantCultureIgnoreCase))
                    throw new ApplicationException("Command cancelled");
                return _lastmessage;
            }
        }

        public async Task<string> AskForString(string question)
        {
            var response = await AskForResponse(question);
            return response.Content;
        }

        public async Task<SocketGuildUser> AskForUser(string question)
        {
            while (true)
            {
                var response = await AskForResponse(question);
                //await response.DeleteAsync();

                // If mentioned, it's easy and we return that
                if (response.MentionedUsers.OfType<SocketGuildUser>().Count() > 0)
                {
                    return response.MentionedUsers.OfType<SocketGuildUser>().First();
                }

                // If not mentioned, we'll search, but first need to download the users
                await _user.Guild.DownloadUsersAsync();

                var user = _user.Guild.Users.Where(u => (u.Nickname ?? u.Username).Contains(response.Content)).FirstOrDefault();
                if (user == null)
                    user = _user.Guild.Users.Where(u => (u.Nickname ?? u.Username).Contains(response.Content, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
                if (user == null)
                    user = _user.Guild.Users.Where(u => u.Username.Contains(response.Content)).FirstOrDefault();
                if (user == null)
                    user = _user.Guild.Users.Where(u => u.Username.Contains(response.Content, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();

                if (user != null)
                    return user;

                var msg = await _channel.SendMessageAsync($"{_prefix} Cannot find a user matching `{response.Content}`, please try to be more specific or @mention the user instead.");
                _sentMessages.Add(msg.Id);
            }
        }

        public async Task<SocketGuildChannel> AskForTextChannel(string question)
        {
            while (true)
            {
                var response = await AskForResponse(question);
                //await response.DeleteAsync();

                // If mentioned, it's easy and we return that
                if (response.MentionedChannels.Count > 0)
                {
                    return response.MentionedChannels.First();
                }

                // If not mentioned, we'll search.
                var channel = _user.Guild.TextChannels.Where(c => c.Name.Contains(response.Content)).FirstOrDefault();
                if (channel == null)
                    channel = _user.Guild.TextChannels.Where(c => c.Name.Contains(response.Content, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();

                if (channel != null)
                    return channel;

                var msg = await _channel.SendMessageAsync($"{_prefix} Cannot find a channel matching `{response.Content}`, please try to be more specific or #mention the channel instead.");
                _sentMessages.Add(msg.Id);
            }
        }

        public async Task<SocketRole> AskForRole(string question)
        {
            while (true)
            {
                var response = await AskForResponse(question);
                //await response.DeleteAsync();

                // If mentioned, it's easy and we return that
                if (response.MentionedRoles.Count > 0)
                {
                    return response.MentionedRoles.First();
                }

                // If not mentioned, we'll search.
                var role = _user.Guild.Roles.Where(r => r.Name.Contains(response.Content)).FirstOrDefault();
                if (role == null)
                    role = _user.Guild.Roles.Where(r => r.Name.Contains(response.Content, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();

                if (role != null)
                    return role;

                var msg = await _channel.SendMessageAsync($"{_prefix} Cannot find a role matching `{response.Content}`, please try to be more specific or @mention the role instead.");
                _sentMessages.Add(msg.Id);
            }
        }

        public void Dispose()
        {
            _client.MessageReceived -= HandleMessageReceived;
            if (_sentMessages.Count > 0)
                _channel.DeleteMessagesAsync(_sentMessages).Forget();
        }
    }
}
