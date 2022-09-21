using Azure.Storage.Queues;
using Betty.Bot.Extensions;
using Betty.Bot.Modules.Twitch;
using Discord;
using Discord.Commands;
using Discord.Commands.Builders;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Betty.Bot.Modules
{
    [Name("Log")]
    [Summary("Summarize and log activity on your discord server. Includes activity tracking and moderator logs.")]
    public class LogCommands : ModuleBase
    {
        private readonly ILogger _logger;
        private readonly DiscordSocketClient _discord;

        private QueueClient _queueClient;

        public LogCommands(ILogger<TwitchCommands> logger, DiscordSocketClient discord)
        {
            _logger = logger;
            _discord = discord;
        }

        protected override void OnModuleBuilding(CommandService commandService, ModuleBuilder builder)
        {
            base.OnModuleBuilding(commandService, builder);

            _discord.ChannelCreated += async (channel) =>
            {
                if (channel is SocketGuildChannel gchannel)
                {
                    await ReportActivity(gchannel.Guild, new EmbedBuilder()
                        .WithTitle("Channel Created")
                        .WithDescription($"Channel `{gchannel.Name}` has been created")
                        .WithCurrentTimestamp()
                        .Build());
                }
            };
            _discord.ChannelDestroyed += async (channel) =>
            {
                if (channel is SocketGuildChannel gchannel)
                {
                    await ReportActivity(gchannel.Guild, new EmbedBuilder()
                        .WithTitle("Channel Deleted")
                        .WithDescription($"Channel `{gchannel.Name}` has been deleted")
                        .WithCurrentTimestamp()
                        .Build());
                }
            };
            _discord.ChannelUpdated += async (before, after) =>
            {
                if (before is SocketGuildChannel gbefore &&
                    after is SocketGuildChannel gafter)
                {
                    await ReportActivity(gafter.Guild, new EmbedBuilder()
                        .WithTitle("Channel Updated")
                        .WithDescription($"Channel `{gbefore.Name}` has been updated")
                        .WithFields((await gbefore.SummarizeChanges(gafter)).Select(c => new EmbedFieldBuilder { Name = c.Key, Value = c.Value, IsInline = true }).ToArray())
                        .WithCurrentTimestamp()
                        .Build());
                }
            };
            _discord.MessageReceived += (message) =>
            {
                return Task.CompletedTask;
            };
            _discord.MessageDeleted += async (msg, channel) =>
            {
                var message = await msg.GetOrDownloadAsync();
                if (message == null)
                    return;

                if (message.Author.IsBot)
                    return;

                if (channel is SocketGuildChannel gchannel)
                {
                    await ReportActivity(gchannel.Guild, new EmbedBuilder()
                        .WithTitle("Message Deleted")
                        .WithDescription($"A message was deleted from channel `{gchannel.Name}`")
                        .AddField("Author", message.Author.SummarizeName(), true)
                        .AddField("Content", message.Content.TrimToMax(EmbedBuilder.MaxDescriptionLength), true)
                        .AddField("CreationDate", message.CreatedAt, true)
                        .WithCurrentTimestamp()
                        .Build());
                }
            };
            _discord.MessageUpdated += async (before, after, channel) =>
            {
                var bmsg = await before.GetOrDownloadAsync();
                if (bmsg == null)
                    return;

                if (bmsg.Author.IsBot)
                    return;

                if (channel is SocketGuildChannel gchannel)
                {
                    await ReportActivity(gchannel.Guild, new EmbedBuilder()
                        .WithTitle("Message Updated")
                        .WithDescription($"A message was updated in channel `{gchannel.Name}`")
                        .WithFields((await bmsg.SummarizeChanges(after)).Select(c => new EmbedFieldBuilder { Name = c.Key, Value = c.Value, IsInline = true }).ToArray())
                        .WithCurrentTimestamp()
                        .Build());
                }
            };
            _discord.MessagesBulkDeleted += async (messages, channel) =>
            {
                foreach (var msg in messages)
                {
                    var dmsg = await msg.GetOrDownloadAsync();
                    if (dmsg == null)
                        continue;

                    if (dmsg.Author.IsBot)
                        continue;

                    if (channel is SocketGuildChannel gchannel)
                    {
                        await ReportActivity(gchannel.Guild, new EmbedBuilder()
                            .WithTitle("Message Deleted (part of a bulk delete)")
                            .WithDescription($"A message was deleted from channel `{gchannel.Name}`")
                            .AddField("Author", dmsg.Author.SummarizeName(), true)
                            .AddField("Content", dmsg.Content.TrimToMax(EmbedBuilder.MaxDescriptionLength), true)
                            .AddField("CreationDate", dmsg.CreatedAt, true)
                            .WithCurrentTimestamp()
                            .Build());
                    }
                }
            };
            _discord.ReactionAdded += (message, channel, reaction) =>
            {
                return Task.CompletedTask;
            };
            _discord.ReactionRemoved += (message, channel, reaction) =>
            {
                return Task.CompletedTask;
            };
            _discord.ReactionsCleared += (message, channel) =>
            {
                return Task.CompletedTask;
            };
            _discord.ReactionsRemovedForEmote += (message, channel, emote) =>
            {
                return Task.CompletedTask;
            };
            _discord.RoleCreated += (role) =>
            {
                return Task.CompletedTask;
            };
            _discord.RoleDeleted += (role) =>
            {
                return Task.CompletedTask;
            };
            _discord.RoleUpdated += (before, after) =>
            {
                return Task.CompletedTask;
            };
            _discord.JoinedGuild += (guild) =>
            {
                return Task.CompletedTask;
            };
            _discord.LeftGuild += (guild) =>
            {
                return Task.CompletedTask;
            };
            _discord.GuildAvailable += (guild) =>
            {
                return Task.CompletedTask;
            };
            _discord.GuildUnavailable += (guild) =>
            {
                return Task.CompletedTask;
            };
            _discord.GuildMembersDownloaded += (guild) =>
            {
                return Task.CompletedTask;
            };
            _discord.GuildUpdated += (before, after) =>
            {
                return Task.CompletedTask;
            };
            _discord.UserJoined += async (guilduser) =>
            {
                await ReportActivity(guilduser.Guild, new EmbedBuilder()
                    .WithTitle("User Joined")
                    .AddField("Name", guilduser.SummarizeName(), true)
                    .AddField("CreatedAt", guilduser.CreatedAt, true)
                    .AddField("IsBot", guilduser.IsBot, true)
                    .AddField("PremiumSince", guilduser.PremiumSince, true)
                    .AddField("PublicFlags", guilduser.PublicFlags, true)
                    .WithThumbnailUrl(guilduser.GetAvatarUrl())
                    .WithCurrentTimestamp()
                    .Build());
            };
            _discord.UserLeft += async (guilduser) =>
            {
                await ReportActivity(guilduser.Guild, new EmbedBuilder()
                    .WithTitle("User Left")
                    .AddField("Name", guilduser.SummarizeName(), true)
                    .AddField("CreatedAt", guilduser.CreatedAt, true)
                    .AddField("IsBot", guilduser.IsBot, true)
                    .AddField("PremiumSince", guilduser.PremiumSince, true)
                    .AddField("PublicFlags", guilduser.PublicFlags, true)
                    .WithThumbnailUrl(guilduser.GetAvatarUrl())
                    .WithCurrentTimestamp()
                    .Build());
            };
            _discord.UserBanned += async (user, guild) =>
            {
                await ReportActivity(guild, new EmbedBuilder()
                    .WithTitle("User Left")
                    .AddField("Name", user.SummarizeName(), true)
                    .AddField("CreatedAt", user.CreatedAt, true)
                    .AddField("IsBot", user.IsBot, true)
                    .AddField("PublicFlags", user.PublicFlags, true)
                    .WithThumbnailUrl(user.GetAvatarUrl())
                    .WithCurrentTimestamp()
                    .Build());
            };
            _discord.UserUnbanned += async (user, guild) =>
            {
                await ReportActivity(guild, new EmbedBuilder()
                    .WithTitle("User Left")
                    .AddField("Name", user.SummarizeName(), true)
                    .AddField("CreatedAt", user.CreatedAt, true)
                    .AddField("IsBot", user.IsBot, true)
                    .AddField("PublicFlags", user.PublicFlags, true)
                    .WithThumbnailUrl(user.GetAvatarUrl())
                    .WithCurrentTimestamp()
                    .Build());
            };
            _discord.UserUpdated += async (before, after) =>
            {
                if (!(before is SocketGuildUser gbefore) ||
                    !(after is SocketGuildUser gafter))
                    return;

                await ReportActivity(gbefore.Guild, new EmbedBuilder()
                    .WithTitle("User Updated")
                    .WithDescription($"User {gafter.SummarizeName()} was updated")
                    .WithFields((await gbefore.SummarizeChanges(gafter)).Select(c => new EmbedFieldBuilder { Name = c.Key, Value = c.Value, IsInline = true }).ToArray())
                    .WithCurrentTimestamp()
                    .Build());
            };
            _discord.GuildMemberUpdated += async (before, after) =>
            {
                await ReportActivity(before.Guild, new EmbedBuilder()
                    .WithTitle("User Updated")
                    .WithDescription($"User {after.SummarizeName()} was updated")
                    .WithFields((await before.SummarizeChanges(after)).Select(c => new EmbedFieldBuilder { Name = c.Key, Value = c.Value, IsInline = true }).ToArray())
                    .WithCurrentTimestamp()
                    .Build());
            };
            _discord.UserVoiceStateUpdated += (user, before, after) =>
            {
                return Task.CompletedTask;
            };
            _discord.VoiceServerUpdated += (server) =>
            {
                return Task.CompletedTask;
            };
            _discord.CurrentUserUpdated += (before, after) =>
            {
                return Task.CompletedTask;
            };
            _discord.UserIsTyping += (user, channel) =>
            {
                return Task.CompletedTask;
            };
            _discord.RecipientAdded += (groupuser) =>
            {
                return Task.CompletedTask;
            };
            _discord.RecipientRemoved += (groupuser) =>
            {
                return Task.CompletedTask;
            };
            _discord.InviteCreated += async (invite) =>
            {
                await ReportActivity(invite.Guild, new EmbedBuilder()
                    .WithTitle("Invite Created")
                    .AddField("Inviter", invite.Inviter?.SummarizeName(), true)
                    .AddField("MaxAge", invite.MaxAge, true)
                    .AddField("MaxUses", invite.MaxUses, true)
                    .AddField("IsTemporary", invite.IsTemporary, true)
                    .AddField("TargetUser", invite.TargetUser?.SummarizeName(), true)
                    .AddField("URL", invite.Url, true)
                    .WithCurrentTimestamp()
                    .Build());
            };
            _discord.InviteDeleted += (channel, code) =>
            {
                return Task.CompletedTask;
            };
            _discord.Connected += () =>
            {
                return Task.CompletedTask;
            };
            _discord.Disconnected += (exception) =>
            {
                return Task.CompletedTask;
            };
            _discord.Ready += () =>
            {
                return Task.CompletedTask;
            };
            _discord.LatencyUpdated += (before, after) =>
            {
                return Task.CompletedTask;
            };
            _discord.Log += (msg) =>
            {
                return Task.CompletedTask;
            };
            _discord.LoggedIn += () =>
            {
                return Task.CompletedTask;
            };
            _discord.LoggedOut += () =>
            {
                return Task.CompletedTask;
            };
        }

        private async Task ReportActivity(IGuild guild, Embed embed)
        {
            await _discord.Guilds.Where(g => g.Name.Contains("NED-Clan")).First().TextChannels.Where(c => c.Name.Contains("activitylog")).First().SendMessageAsync(embed: embed);
        }

        [Command("log activity")]
        [RequireUserPermission(GuildPermission.Administrator)]
        [RequireBotPermission(ChannelPermission.ManageChannels)]
        public async Task AddCommand(string username = null, IGuildChannel channel = null, [Remainder] string message = null)
        {
            //await ReplyAsync($"{prefix} Announcement for Twitch user {user.DisplayName} has been added for <#{channel.Id}>");
        }

        //[Command("log test")]
        //[RequireUserPermission(GuildPermission.Administrator)]
        //public async Task TestCommand()
        //{
        //    var guildId = Context.Guild.Id;
        //    var channelId = Context.Channel.Id;
        //    var userId = Context.User.Id;

        //    var apiClient = typeof(Discord.Rest.BaseDiscordClient).GetProperty("ApiClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        //    var sendAsync = apiClient.GetType().GetMethod("SendAsync", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        //    await sendAsync.Invoke(apiClient, new object[] { "GET", () => $"streams/guild:{guildId}:{channelId}:{userId}/preview?version={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}" });

        //    //discord

        //    //_discord.GetType().
        //}

    }
}
