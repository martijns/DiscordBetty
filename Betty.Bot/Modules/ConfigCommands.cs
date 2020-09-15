using Azure.Core.Pipeline;
using Azure.Storage.Queues;
using Betty.Bot.Extensions;
using Betty.Bot.Services;
using Betty.Entities.Twitch;
using Discord;
using Discord.Commands;
using Discord.Commands.Builders;
using Discord.WebSocket;
using Microsoft.Azure.Cosmos.Table;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
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
    public class ConfigCommands : ModuleBase
    {
        private static readonly Random Random = new Random();

        private readonly ILogger _logger;
        private readonly IPrefixService _prefix;

        public ConfigCommands(ILogger<TwitchCommands> logger, IPrefixService prefix)
        {
            _logger = logger;
            _prefix = prefix;
        }

        protected override void OnModuleBuilding(CommandService commandService, ModuleBuilder builder)
        {
            base.OnModuleBuilding(commandService, builder);
        }

        [Command("config prefix")]
        [RequireUserPermission(GuildPermission.Administrator)]
        public async Task PrefixCommand(string prefix = null)
        {
            if (string.IsNullOrEmpty(prefix))
            {
                await ReplyAsync($"The currently configured prefix is: `{await _prefix.GetPrefix(Context.Guild)}`");
                return;
            }

            if (prefix.Length > 1)
            {
                await ReplyAsync($"Prefix `{prefix}` is invalid. A prefix must be a single character, not multiple.");
                return;
            }

            await _prefix.SetPrefix(Context.Guild, prefix);
            await ReplyAsync($"Prefix `{prefix}` has been successfully set.");
        }
    }
}
