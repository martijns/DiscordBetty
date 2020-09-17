using Azure.Core.Pipeline;
using Azure.Storage.Queues;
using Betty.Bot.Extensions;
using Betty.Bot.Services;
using Betty.Entities.Twitch;
using Discord;
using Discord.Commands;
using Discord.Commands.Builders;
using Discord.WebSocket;
using ImageMagick;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Azure.Cosmos.Table;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TwitchLib.Api;
using TwitchLib.Api.Core.Enums;
using TwitchLib.Api.Helix.Models.Games;
using TwitchLib.Api.Helix.Models.Streams;
using TwitchLib.Api.Helix.Models.Subscriptions;
using TwitchLib.Api.Helix.Models.Users;

namespace Betty.Bot.Modules.Twitch
{
    public class TwitchCommands : ModuleBase
    {
        private static readonly Random Random = new Random();
        private static readonly string FallbackGameId = "509658"; // Just Chatting

        private readonly ILogger _logger;
        private readonly IConfiguration _config;
        private readonly TwitchAPI _twitch;
        private readonly IImageHosting _imagehost;
        private readonly IKeyValueStore _kvstore;
        private readonly DiscordSocketClient _discord;

        private QueueClient _queueClient;

        public TwitchCommands(ILogger<TwitchCommands> logger, IConfiguration config, TwitchAPI twitch, IImageHosting imagehost, IKeyValueStore kvstore, DiscordSocketClient discord)
        {
            _logger = logger;
            _config = config;
            _twitch = twitch;
            _imagehost = imagehost;
            _kvstore = kvstore;
            _discord = discord;
        }

        protected override void OnModuleBuilding(CommandService commandService, ModuleBuilder builder)
        {
            base.OnModuleBuilding(commandService, builder);

            _queueClient = new QueueClient(_config["AzureStorageAccount"], "twitch");
            Task.Factory.StartNew(LongRunningMessageReceiveTask, TaskCreationOptions.LongRunning);
            Task.Factory.StartNew(LongRunningOncePerHourTasks, TaskCreationOptions.LongRunning);
            Task.Factory.StartNew(LongRunningOncePerMinuteTasks, TaskCreationOptions.LongRunning);

        }

        private async Task LongRunningMessageReceiveTask()
        {
            _logger.LogDebug($"Started {nameof(LongRunningMessageReceiveTask)}");
            try
            {
                await _queueClient.CreateIfNotExistsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Error calling CreateIfNotExistsAsync during start of MessageReceiveThread. Continuing anyway.");
            }

            while (true)
            {
                try
                {
                    var response = await _queueClient.ReceiveMessagesAsync(32);
                    foreach (var message in response.Value)
                    {
                        var json = Encoding.UTF8.GetString(Convert.FromBase64String(message.MessageText));
                        _logger.LogDebug($"Processing twitch message: {json}");
                        var msg = JsonConvert.DeserializeObject<HttpCallbackMessage>(json);
                        var userid = msg.QueryItems["user_id"];
                        var username = msg.QueryItems["user_name"];
                        var cbtype = msg.QueryItems["cbtype"];
                        switch (cbtype)
                        {
                            case "stream":
                                await ProcessStreamMessage(userid, msg.RequestBody);
                                break;
                            default:
                                _logger.LogWarning($"Unrecognized callback type '{cbtype}', cannot process.");
                                break;
                        }
                        await _queueClient.DeleteMessageAsync(message.MessageId, message.PopReceipt);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Error in {nameof(LongRunningMessageReceiveTask)}");
                }
                await Task.Delay(10000);
            }
        }

        private async Task LongRunningOncePerHourTasks()
        {
            _logger.LogDebug($"Started {nameof(LongRunningOncePerHourTasks)}");
            while (true)
            {
                try
                {
                    await VerifySubscriptions();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Error in {nameof(LongRunningOncePerHourTasks)}");
                }
                await Task.Delay(60 * 60 * 1000);
            }
        }

        private async Task LongRunningOncePerMinuteTasks()
        {
            _logger.LogDebug($"Started {nameof(LongRunningOncePerMinuteTasks)}");
            while (true)
            {
                try
                {
                    await CheckAndCreateSnapshots();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Error in {nameof(LongRunningOncePerMinuteTasks)}");
                }
                await Task.Delay(60 * 1000);
            }
        }

        private async Task VerifySubscriptions()
        {
            // Get all existing subscriptions
            var allsubs = new List<TwitchLib.Api.Helix.Models.Webhooks.Subscription>();
            string cursor = null;
            do
            {
                var subsresp = await _twitch.Helix.Webhooks.GetWebhookSubscriptionsAsync(after: cursor, first: 100);
                allsubs.AddRange(subsresp.Subscriptions);
                cursor = subsresp.Pagination.Cursor;
            } while (cursor != null);
            _logger.LogInformation($"Found {allsubs.Count} active subscriptions");

            // Get all streamers we should sub on
            var allstreamers = await _kvstore.GetValuesAsync<StreamerState>("twitch");
            var substreamers = allstreamers.Values.Where(s => s.Announcements.Count > 0).ToList();
            _logger.LogInformation($"Found {substreamers.Count} streamers we should be subscribed on");

            // Check if we have a subscription
            foreach (var streamer in substreamers)
            {
                var topic = $"https://api.twitch.tv/helix/streams?user_id={streamer.Id}";
                var callback = _config["TwitchCallbackUrl"] + $"&user_id={streamer.Id}&user_name={streamer.UserName}&cbtype=stream";
                var signingsecret = _config["TwitchCallbackSigningSecret"];
                var sub = allsubs.Where(s => s.Topic == topic).FirstOrDefault();
                if (sub != null)
                {
                    if (sub.Callback != callback)
                    {
                        _logger.LogInformation($"We have a working subscription on {streamer.UserName}, but the callback URL is incorrect. Unsubscribing and resubscribing...");
                        await _twitch.Helix.Webhooks.StreamUpDownAsync(sub.Callback, WebhookCallMode.Unsubscribe, streamer.Id, TimeSpan.FromSeconds(864000), signingSecret: signingsecret);
                        await _twitch.Helix.Webhooks.StreamUpDownAsync(callback, WebhookCallMode.Subscribe, streamer.Id, TimeSpan.FromSeconds(864000), signingSecret: signingsecret);
                    }
                    else if (DateTime.UtcNow > sub.ExpiresAt.AddHours(-2))
                    {
                        _logger.LogInformation($"We have a working subscription on {streamer.UserName}, but it expires within 2 hours. Renewing...");
                        await _twitch.Helix.Webhooks.StreamUpDownAsync(callback, WebhookCallMode.Subscribe, streamer.Id, TimeSpan.FromSeconds(864000), signingSecret: signingsecret);
                    }
                    else
                    {
                        _logger.LogDebug($"We have a working subscription on {streamer.UserName} and its expiration is far enough away ({sub.ExpiresAt})");
                    }
                }
                else
                {
                    _logger.LogInformation($"We currently have no subscription for {streamer.UserName}. Subscribing...");
                    await _twitch.Helix.Webhooks.StreamUpDownAsync(callback, WebhookCallMode.Subscribe, streamer.Id, TimeSpan.FromSeconds(864000), signingSecret: signingsecret);
                }
            }
        }

        private async Task CheckAndCreateSnapshots()
        {
            var allstreamers = await _kvstore.GetValuesAsync<StreamerState>("twitch");
            var livestreamers = allstreamers.Values.Where(s => s.Announcements.Count > 0 && s.IsLive).ToList();
            _logger.LogDebug($"There are {livestreamers.Count} streamers online: {string.Join(",", livestreamers.Select(s => s.DisplayName))}");

            foreach (var streamer in livestreamers)
            {
                // Only create snapshots if the most recent one is older than 5 minutes
                if (streamer.Snapshots.Last().SnapshotTime.AddMinutes(5) > DateTime.UtcNow)
                    continue;
                _logger.LogInformation($"Creating snapshot for {streamer.DisplayName}");

                // Fetch the latest stream status
                var streamResp = await _twitch.Helix.Streams.GetStreamsAsync(userIds: new List<string> { streamer.Id });
                if (streamResp.Streams.Length == 0)
                {
                    // Seems this streamer went offline?
                    await ProcessStreamMessage(streamer.Id, JsonConvert.SerializeObject(streamResp));
                    continue;
                }
                var stream = streamResp.Streams.First();

                // Create a new thumbnail
                var streamThumbnail = await _imagehost.UploadImageFromUrl(streamer.ThumbnailTemplateUrl.Replace("{width}", "1280").Replace("{height}", "720") + $"?random={Random.Next(1000000, 9999999)}");

                // Add the snapshot
                streamer.Snapshots.Add(new Snapshot
                {
                    SnapshotTime = DateTime.UtcNow,
                    GameId = stream.GameId,
                    ThumbnailUrl = streamThumbnail,
                    Title = stream.Title,
                    ViewerCount = stream.ViewerCount
                });
                await _kvstore.SetValueAsync("twitch", streamer.Id, streamer);

                // Update announcements
                foreach (var announcement in streamer.Announcements)
                {
                    // If we never sent an 'online' message, we won't bother with an 'offline' message.
                    if (announcement.LastMessageId == 0)
                        continue;

                    var guild = _discord.GetGuild(announcement.GuildId);
                    var channel = guild.GetTextChannel(announcement.ChannelId);
                    var message = await channel.GetMessageAsync(announcement.LastMessageId);
                    if (message is IUserMessage userMessage)
                    {
                        var msg = await GetAnnouncement(guild, streamer, announcement.AnnouncementText, streamer.Snapshots.Last().ThumbnailUrl, online: true);
                        await userMessage.ModifyAsync(props =>
                        {
                            props.Content = msg.Key;
                            props.Embed = msg.Value;
                        });
                    }
                }
            }
        }

        private async Task<string> CreateAnimatedGif(IEnumerable<Snapshot> snapshots)
        {
            _logger.LogInformation($"Using the following snapshots to create an animated gif: {JsonConvert.SerializeObject(snapshots)}");
            var tmpfiles = new List<string>();
            using (var webclient = new WebClient())
            using (var collection = new MagickImageCollection())
            {
                // Skip the first snapshot if we have more than 1, on most streams it will be the "starting soon" screen
                if (snapshots.Count() > 1)
                    snapshots = snapshots.Skip(1);

                // Load all the thumbs for the .gif
                foreach (var snapshot in snapshots)
                {
                    var ext = Path.GetExtension(snapshot.ThumbnailUrl);
                    var tmppath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n") + ext);
                    tmpfiles.Add(tmppath);
                    webclient.DownloadFile(snapshot.ThumbnailUrl, tmppath);
                    int current = collection.Count;
                    collection.Add(tmppath);
                    collection[current].AnimationDelay = 50; // 0.5 seconds
                }

                // The delay on the latest should be longer
                collection.Last().AnimationDelay = 200; // 2 seconds

                // Resize to a more sensible size
                foreach (MagickImage image in collection)
                {
                    image.Resize(400, 0);
                }

                // Reduce colors to reduce filesize
                var settings = new QuantizeSettings
                {
                    Colors = 256,
                };
                collection.Quantize(settings);

                // Optimize, only works if all thumbs are the same size
                collection.Optimize();

                // Generate the .gif
                var tmpgifpath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n") + ".gif");
                tmpfiles.Add(tmpgifpath);
                collection.Write(tmpgifpath);

                // Upload and save the URL
                var url = await _imagehost.UploadImageFromFile(tmpgifpath);

                // Clean old files
                foreach (var file in tmpfiles)
                {
                    File.Delete(file);
                }

                return url;
            }
        }

        private async Task ProcessStreamMessage(string userid, string body)
        {
            // Get the user via API
            var userResp = await _twitch.Helix.Users.GetUsersAsync(ids: new List<string> { userid }).ConfigureAwait(true);
            if (userResp.Users.Length == 0)
                throw new ApplicationException($"Cannot find a user with userid: {userid}");
            var user = userResp.Users.First();

            // Get the state, and update it with the latest information
            var state = await _kvstore.GetValueAsync<StreamerState>("twitch", user.Id);
            state.UserName = user.Login;
            state.DisplayName = user.DisplayName;
            state.Description = user.Description;
            state.ProfileImageUrl = user.ProfileImageUrl;
            state.OfflineImageUrl = user.OfflineImageUrl;
            state.ViewCount = user.ViewCount;

            // Decide what to do depending on what we received and what our state is
            var streams = JsonConvert.DeserializeObject<GetStreamsResponse>(body);
            var stream = streams.Streams.FirstOrDefault();
            var correctedGameId = string.IsNullOrEmpty(stream?.GameId) ? FallbackGameId : stream.GameId; // We cannot handle empty "game_id"
            
            if (!state.IsLive && stream != null) // User just went live
            {
                // Get game information
                var gameResp = await _twitch.Helix.Games.GetGamesAsync(gameIds: new List<string> { correctedGameId }).ConfigureAwait(true);
                if (gameResp.Games.Length == 0)
                    throw new ApplicationException($"Cannot find a game with gameid: {correctedGameId}");
                var game = gameResp.Games.First();

                // Update fields
                state.IsLive = true;
                state.ThumbnailTemplateUrl = stream.ThumbnailUrl;
                state.WentLiveAt = stream.StartedAt;
                state.WentOfflineAt = null;
                state.CurrentTitle = stream.Title;
                state.CurrentGameName = game.Name;
                state.CurrentGameBoxArtUrl = game.BoxArtUrl;

                // Prepare current images
                var streamThumbnail = await _imagehost.UploadImageFromUrl(state.ThumbnailTemplateUrl.Replace("{width}", "1280").Replace("{height}", "720") + $"?random={Random.Next(1000000, 9999999)}");

                // Initial snapshot
                state.Snapshots.Clear();
                state.Snapshots.Add(new Snapshot
                {
                    SnapshotTime = DateTime.UtcNow,
                    GameId = correctedGameId,
                    ThumbnailUrl = streamThumbnail,
                    Title = state.Description,
                    ViewerCount = stream.ViewerCount
                });

                // Send announcements
                foreach (var announcement in state.Announcements)
                {
                    var guild = _discord.GetGuild(announcement.GuildId);
                    var channel = guild.GetTextChannel(announcement.ChannelId);
                    var msg = await GetAnnouncement(guild, state, announcement.AnnouncementText, streamThumbnail, online: true);
                    var message = await channel.SendMessageAsync(msg.Key, isTTS: false, embed: msg.Value);
                    announcement.LastMessageId = message.Id;
                }
            }
            else if (state.IsLive && stream != null) // User is already live, but we received an update. Something probably changed.
            {
                // Get game information
                var gameResp = await _twitch.Helix.Games.GetGamesAsync(gameIds: new List<string> { correctedGameId }).ConfigureAwait(true);
                if (gameResp.Games.Length == 0)
                    throw new ApplicationException($"Cannot find a game with gameid: {correctedGameId}");
                var game = gameResp.Games.First();

                // Update fields
                state.IsLive = true;
                state.ThumbnailTemplateUrl = stream.ThumbnailUrl;
                state.WentLiveAt = stream.StartedAt;
                state.WentOfflineAt = null;
                state.CurrentTitle = stream.Title;
                state.CurrentGameName = game.Name;
                state.CurrentGameBoxArtUrl = game.BoxArtUrl;

                // Update announcements
                foreach (var announcement in state.Announcements)
                {
                    // If we never sent an 'online' message, we won't bother with an 'offline' message.
                    if (announcement.LastMessageId == 0)
                        continue;

                    var guild = _discord.GetGuild(announcement.GuildId);
                    var channel = guild.GetTextChannel(announcement.ChannelId);
                    var message = await channel.GetMessageAsync(announcement.LastMessageId);
                    if (message is IUserMessage userMessage)
                    {
                        var msg = await GetAnnouncement(guild, state, announcement.AnnouncementText, state.Snapshots.Last().ThumbnailUrl, online: true);
                        await userMessage.ModifyAsync(props =>
                        {
                            props.Content = msg.Key;
                            props.Embed = msg.Value;
                        });
                    }
                }
            }
            else if (state.IsLive && stream == null)
            {
                // User was live, but went offline
                state.IsLive = false;
                state.WentOfflineAt = DateTime.UtcNow;

                // Update announcements
                foreach (var announcement in state.Announcements)
                {
                    // If we never sent an 'online' message, we won't bother with an 'offline' message.
                    if (announcement.LastMessageId == 0)
                        continue;

                    var guild = _discord.GetGuild(announcement.GuildId);
                    var channel = guild.GetTextChannel(announcement.ChannelId);
                    var message = await channel.GetMessageAsync(announcement.LastMessageId);
                    if (message is IUserMessage userMessage)
                    {
                        var anigifUrl = await CreateAnimatedGif(state.Snapshots);
                        var msg = await GetAnnouncement(guild, state, announcement.AnnouncementText, anigifUrl, online: false);
                        await userMessage.ModifyAsync(props =>
                        {
                            props.Content = msg.Key;
                            props.Embed = msg.Value;
                        });
                    }
                }
            }
            else // !state.IsLive && stream == null
            {
                // User was already offline and we got another message confirming this?
                // We don't handle this for now
            }

            // Store the new state
            await _kvstore.SetValueAsync("twitch", state.Id, state);
        }

        private Task<KeyValuePair<string, Embed>> GetAnnouncement(IGuild guild, StreamerState stream, string announceTemplate, string streamThumbnail, bool online = true)
        {
            var gameThumbnail = stream.CurrentGameBoxArtUrl.Replace("{width}", "300").Replace("{height}", "400").Replace("/./", "/");

            string announcementText;
            if (online)
                announcementText = ":green_circle: " + announceTemplate + " :green_circle:";
            else
                announcementText = $":red_circle: **{{name}}** went offline :red_circle:";
            announcementText = announcementText
                .Replace("{everyone}", guild.EveryoneRole.Mention)
                .Replace("{name}", stream.DisplayName)
                .Replace("{game}", stream.CurrentGameName)
                .Replace("{link}", $"https://twitch.tv/{stream.UserName}");

            string footerText;
            DateTimeOffset timestamp;
            if (stream.IsLive)
            {
                var timespan = DateTime.UtcNow - (stream.WentLiveAt ?? DateTime.UtcNow);
                footerText = $"Streaming {timespan.ToFriendlyDisplay(2)}. Stream started at ⫸";
                timestamp = stream.WentLiveAt ?? DateTime.UtcNow;
            }
            else
            {
                var timespan = (stream.WentOfflineAt ?? DateTime.UtcNow) - (stream.WentLiveAt ?? DateTime.UtcNow);
                footerText = $"Streamed for {timespan.ToFriendlyDisplay(2)}. Stream went offline at ⫸";
                timestamp = stream.WentOfflineAt ?? DateTime.UtcNow;
            }

            var embed = new EmbedBuilder()
            {
                Title = stream.CurrentTitle.Length >= EmbedBuilder.MaxTitleLength ? stream.CurrentTitle.Substring(0, 255) : stream.CurrentTitle,
                Author = new EmbedAuthorBuilder
                {
                    Name = stream.DisplayName,
                    IconUrl = stream.ProfileImageUrl,
                    Url = $"https://twitch.tv/{stream.UserName}"
                },
                Url = $"https://twitch.tv/{stream.UserName}",
                Color = online ? Color.Green : Color.Red,
                ThumbnailUrl = gameThumbnail,
                ImageUrl = streamThumbnail,
                Fields = new List<EmbedFieldBuilder>
                {
                    new EmbedFieldBuilder { Name = "Game", Value = stream.CurrentGameName },
                    new EmbedFieldBuilder { Name = "Status", Value = online ? "Online" : "Offline" }
                },
                Footer = new EmbedFooterBuilder
                {
                    Text = footerText
                },
                Timestamp = timestamp
            }.Build();

            return Task.FromResult(new KeyValuePair<string, Embed>(announcementText, embed));
        }

        [Command("twitch add")]
        [RequireUserPermission(GuildPermission.Administrator)]
        public async Task AddCommand(string username = null, IGuildChannel channel = null, [Remainder] string message = null)
        {
            var prefix = $"**[twitch add]**";

            var me = await Context.Guild.GetCurrentUserAsync();
            var missingPermissions = new List<string>();
            if (!me.GuildPermissions.MentionEveryone)
                missingPermissions.Add(nameof(me.GuildPermissions.MentionEveryone));
            if (!me.GuildPermissions.ManageChannels)
                missingPermissions.Add(nameof(me.GuildPermissions.ManageChannels));
            if (!me.GuildPermissions.ReadMessageHistory)
                missingPermissions.Add(nameof(me.GuildPermissions.ReadMessageHistory));
            if (missingPermissions.Count > 0)
            {
                await ReplyAsync($"{prefix} Before we start. I'm a bot currently in development. I don't have very fine-tuned permission management or proper error handling for missing permissions yet. As such, I'll ask for a little more than I'd need in the future. Please grant me the following permissions:\n`{string.Join(", ", missingPermissions)}`");
                return;
            }

            //var role = Context.Guild.Roles.Where(r => r.Name == "").FirstOrDefault() as SocketRole;
            //var user = role.Members.FirstOrDefault() as IGuildUser;
            //user.Activity.Type == ActivityType.Streaming
            //IConnection

            using var q = new Questionair(Context, prefix);
            username ??= await q.AskForString($"What is the twitch username of the streamer you wish to announce?");
            channel ??= await q.AskForTextChannel($"To which channel should the announcement be posted?");
            message ??= await q.AskForString($"What should the announcement look like?" +
                $"\n**Example**: Hi all, {{name}} just went online with {{game}}! Go check it out at {{link}}" +
                $"\n*You can use the following placeholders:*" +
                $"\n*{{everyone}} to ping everyone (and avoid pinging while configuring the bot)*" +
                $"\n*{{name}} to reference the user*" +
                $"\n*{{game}} to reference the game*" +
                $"\n*{{link}} to reference the link to visit the channel*"
                );

            var usersResp = await _twitch.Helix.Users.GetUsersAsync(logins: new List<string>{ username });
            if (usersResp.Users.Length == 0)
            {
                await ReplyAsync($"{prefix} User {username} cannot be found");
                return;
            }
            var user = usersResp.Users.First();

            bool isnew = false;
            var state = await _kvstore.GetValueAsync<StreamerState>("twitch", user.Id);
            if (state == null)
            {
                state = new StreamerState();
                isnew = true;
            }
            state.Id = user.Id;
            state.UserName = user.Login;
            state.DisplayName = user.DisplayName;
            state.Description = user.Description;
            state.ProfileImageUrl = user.ProfileImageUrl;
            state.OfflineImageUrl = user.OfflineImageUrl;
            state.ViewCount = user.ViewCount;
            state.Announcements.Add(new Announcement
            {
                GuildId = Context.Guild.Id,
                ChannelId = channel.Id,
                AnnouncementText = message
            });
            await _kvstore.SetValueAsync("twitch", user.Id, state);

            // Check subscriptions if we added a streamer we didn't know yet
            if (isnew)
            {
                await VerifySubscriptions();
            }

            await ReplyAsync($"{prefix} Announcement for Twitch user {user.DisplayName} has been added for <#{channel.Id}>");
        }

        [Command("twitch remove")]
        [RequireUserPermission(GuildPermission.Administrator)]
        public async Task RemoveCommand(string username = null)
        {
            var prefix = $"**[twitch remove]**";
            using var q = new Questionair(Context, prefix);
            username ??= await q.AskForString($"Which user do you want to remove announcements for?");

            var usersResp = await _twitch.Helix.Users.GetUsersAsync(logins: new List<string> { username });
            if (usersResp.Users.Length == 0)
            {
                await ReplyAsync($"{prefix} User {username} cannot be found");
                return;
            }
            var user = usersResp.Users.First();

            var state = await _kvstore.GetValueAsync<StreamerState>("twitch", user.Id);
            if (state == null)
            {
                await ReplyAsync($"{prefix} I've never seen this Twitch user before");
                return;
            }

            if (!state.Announcements.Where(a => a.GuildId == Context.Guild.Id).Any())
            {
                await ReplyAsync($"{prefix} This Twitch user has no announcements on your server");
                return;
            }

            state.Announcements.RemoveAll(a => a.GuildId == Context.Guild.Id);
            await _kvstore.SetValueAsync("twitch", user.Id, state);
            await ReplyAsync($"{prefix} All announcements for Twitch user {state.DisplayName} have been removed");
        }

        [Command("twitch list")]
        //[RequireUserPermission(GuildPermission.Administrator)]
        public async Task ListCommand()
        {
            var prefix = $"**[twitch list]**";

            var allstreamers = await _kvstore.GetValuesAsync<StreamerState>("twitch");
            var guildstreamers = allstreamers.Values.Where(s => s.Announcements.Where(a => a.GuildId == Context.Guild.Id).Any());
            if (!guildstreamers.Any())
            {
                await ReplyAsync($"{prefix} There are no Twitch users with announcements in your server");
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append($"{prefix} The following Twitch announcements are configured in your server:");
            foreach (var streamer in guildstreamers)
            {
                var announcements = streamer.Announcements.Where(a => a.GuildId == Context.Guild.Id);
                foreach (var announcement in announcements)
                {
                    sb.Append($"\n⫸ {streamer.DisplayName} ⫸ <#{announcement.ChannelId}> ⫸ {announcement.AnnouncementText}");
                }
            }
            await ReplyAsync(sb.ToString());
        }

        [Command("twitch previewonline")]
        //[RequireUserPermission(GuildPermission.Administrator)]
        public async Task PreviewOnlineCommand(string username = null)
        {
            var prefix = $"**[twitch previewonline]**";
            using var q = new Questionair(Context, prefix);
            username ??= await q.AskForString($"Preview which user?");

            // Find user
            var usersResp = await _twitch.Helix.Users.GetUsersAsync(logins: new List<string> { username });
            if (usersResp.Users.Length == 0)
            {
                await ReplyAsync($"{prefix} User {username} cannot be found");
                return;
            }
            var user = usersResp.Users.First();

            var state = await _kvstore.GetValueAsync<StreamerState>("twitch", user.Id);
            if (state == null || string.IsNullOrEmpty(state.CurrentGameBoxArtUrl))
            {
                // Find game
                var gameResp = await _twitch.Helix.Games.GetGamesAsync(gameIds: new List<string> { "32399" }).ConfigureAwait(true);
                if (gameResp.Games.Length == 0)
                    throw new ApplicationException($"Cannot find a game with gameid: {32399}");
                var game = gameResp.Games.First();

                state = new StreamerState
                {
                    Id = user.Id,
                    UserName = user.Login,
                    DisplayName = user.DisplayName,
                    ProfileImageUrl = user.ProfileImageUrl,
                    CurrentTitle = "**DUMMY** This is the fancy title of my stream! Come and join me!",
                    CurrentGameName = game.Name,
                    CurrentGameBoxArtUrl = game.BoxArtUrl,
                    ThumbnailTemplateUrl = $"https://static-cdn.jtvnw.net/previews-ttv/live_user_{user.Login}-{{width}}x{{height}}.jpg",
                    Description = user.Description,
                    OfflineImageUrl = user.OfflineImageUrl,
                    IsLive = true,
                    ViewCount = 420,
                    WentLiveAt = DateTime.UtcNow,
                    WentOfflineAt = null
                };
            }

            var snapshot = state.Snapshots.Count > 0 ? state.Snapshots.Last().ThumbnailUrl : user.OfflineImageUrl;
            var msg = await GetAnnouncement(Context.Guild, state, "**DUMMY** Hi all, {name} just went online with {game}! Go check it out at {link}", snapshot, true);
            await Context.Channel.SendMessageAsync(msg.Key, isTTS: false, embed: msg.Value);
        }

        [Command("twitch previewoffline")]
        //[RequireUserPermission(GuildPermission.Administrator)]
        public async Task PreviewOfflineCommand(string username = null)
        {
            var prefix = $"**[twitch previewonline]**";
            using var q = new Questionair(Context, prefix);
            username ??= await q.AskForString($"Preview which user?");

            // Find user
            var usersResp = await _twitch.Helix.Users.GetUsersAsync(logins: new List<string> { username });
            if (usersResp.Users.Length == 0)
            {
                await ReplyAsync($"{prefix} User {username} cannot be found");
                return;
            }
            var user = usersResp.Users.First();

            var state = await _kvstore.GetValueAsync<StreamerState>("twitch", user.Id);
            if (state == null || string.IsNullOrEmpty(state.CurrentGameBoxArtUrl))
            {
                // Find game
                var gameResp = await _twitch.Helix.Games.GetGamesAsync(gameIds: new List<string> { "32399" }).ConfigureAwait(true);
                if (gameResp.Games.Length == 0)
                    throw new ApplicationException($"Cannot find a game with gameid: {32399}");
                var game = gameResp.Games.First();

                state = new StreamerState
                {
                    Id = user.Id,
                    UserName = user.Login,
                    DisplayName = user.DisplayName,
                    ProfileImageUrl = user.ProfileImageUrl,
                    CurrentTitle = "**DUMMY** This is the fancy title of my stream! Come and join me!",
                    CurrentGameName = game.Name,
                    CurrentGameBoxArtUrl = game.BoxArtUrl,
                    ThumbnailTemplateUrl = $"https://static-cdn.jtvnw.net/previews-ttv/live_user_{user.Login}-{{width}}x{{height}}.jpg",
                    Description = user.Description,
                    OfflineImageUrl = user.OfflineImageUrl,
                    IsLive = false,
                    ViewCount = 420,
                    WentLiveAt = DateTime.UtcNow.AddHours(-1),
                    WentOfflineAt = DateTime.UtcNow
                };
            }

            // Fix state in case user was online
            if (state.IsLive)
            {
                state.IsLive = false;
                state.WentOfflineAt = (state.WentLiveAt ?? DateTime.UtcNow).AddHours(1);
            }

            var snapshot = state.Snapshots.Count > 0 ? state.Snapshots.Last().ThumbnailUrl : user.OfflineImageUrl;
            var msg = await GetAnnouncement(Context.Guild, state, "**DUMMY** Hi all, {name} just went online with {game}! Go check it out at {link}", snapshot, false);
            await Context.Channel.SendMessageAsync(msg.Key, isTTS: false, embed: msg.Value);
        }

        public class StreamerState
        {
            public string Id { get; set; }
            public string UserName { get; set; }
            public string DisplayName { get; set; }
            public string Description { get; set; }
            public string ProfileImageUrl { get; set; }
            public string OfflineImageUrl { get; set; }
            public string ThumbnailTemplateUrl { get; set; }
            public long ViewCount { get; set; }
            public List<Announcement> Announcements { get; set; } = new List<Announcement>();
            public bool IsLive { get; set; }
            public DateTime? WentLiveAt { get; set; }
            public DateTime? WentOfflineAt { get; set; }
            public string CurrentTitle { get; set; }
            public string CurrentGameName { get; set; }
            public string CurrentGameBoxArtUrl { get; set; }
            public List<Snapshot> Snapshots { get; set; } = new List<Snapshot>();
        }

        public class Announcement
        {
            public ulong GuildId { get; set; }
            public ulong ChannelId { get; set; }
            public string AnnouncementText { get; set; }
            public ulong LastMessageId { get; set; }
        }

        public class Snapshot
        {
            public DateTime SnapshotTime { get; set; }
            public string GameId { get; set; }
            public string Title { get; set; }
            public long ViewerCount { get; set; }
            public string ThumbnailUrl { get; set; }
        }
    }
}
