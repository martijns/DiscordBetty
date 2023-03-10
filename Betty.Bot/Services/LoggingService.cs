using Discord;
using Discord.Commands;
using Discord.WebSocket;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System;
using Betty.Bot.Extensions;

namespace Betty.Bot.Services
{
    /// <summary>
    /// Based upon: https://github.com/gngrninja/csharpi/tree/03-logging
    /// </summary>
    public class LoggingService
    {

        // declare the fields used later in this class
        private readonly ILogger _logger;
        private readonly DiscordSocketClient _discord;
        private readonly CommandService _commands;

        public LoggingService(IServiceProvider services)
        {
            // get the services we need via DI, and assign the fields declared above to them
            _discord = services.GetRequiredService<DiscordSocketClient>();
            _commands = services.GetRequiredService<CommandService>();
            _logger = services.GetRequiredService<ILogger<LoggingService>>();

            // hook into these events with the methods provided below
            _discord.Ready += OnReadyAsync;
            _discord.Log += OnLogAsync;
            _commands.Log += OnLogAsync;
        }

        // this method executes on the bot being connected/ready
        public async Task OnReadyAsync()
        {
            _logger.LogInformation($"Connected as -> [{_discord.CurrentUser}] :)");
            _logger.LogInformation($"We are on [{_discord.Guilds.Count}] servers:");
            foreach (var guild in _discord.Guilds)
            {
                await guild.DownloadUsersAsync();
                var owner = guild.Owner != null ? await guild.Owner.SummarizeName() : string.Empty;
                _logger.LogInformation($" - {guild.Id}/{guild.Name} owned by {owner} with permissions: {string.Join(",", guild.CurrentUser?.GuildPermissions.ToList())}");
            }
        }

        // this method switches out the severity level from Discord.Net's API, and logs appropriately
        public Task OnLogAsync(LogMessage msg)
        {
            string logText = $"{msg.Source}: {msg.Exception?.ToString() ?? msg.Message}";
            switch (msg.Severity)
            {
                case LogSeverity.Critical:
                    _logger.LogCritical(logText);
                    break;
                case LogSeverity.Warning:
                    _logger.LogWarning(logText);
                    break;
                case LogSeverity.Info:
                    _logger.LogInformation(logText);
                    break;
                case LogSeverity.Verbose:
                    _logger.LogInformation(logText);
                    break;
                case LogSeverity.Debug:
                    _logger.LogDebug(logText);
                    break;
                case LogSeverity.Error:
                    _logger.LogError(logText);
                    break;
                default:
                    _logger.LogError($"Unsupported log severity {msg.Severity}: {logText}");
                    break;
            }

            return Task.CompletedTask;

        }
    }
}
