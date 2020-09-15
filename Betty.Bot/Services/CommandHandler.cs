using System;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Betty.Bot.Extensions;
using System.Linq;

namespace Betty.Bot.Services
{
    /// <summary>
    /// Based upon: https://github.com/gngrninja/csharpi/tree/03-logging
    /// </summary>
    public class CommandHandler
    {
        // setup fields to be set later in the constructor
        private readonly IConfiguration _config;
        private readonly CommandService _commands;
        private readonly DiscordSocketClient _client;
        private readonly IServiceProvider _services;
        private readonly ILogger _logger;
        private readonly IPrefixService _prefix;

        public CommandHandler(IServiceProvider services)
        {
            // juice up the fields with these services
            // since we passed the services in, we can use GetRequiredService to pass them into the fields set earlier
            _config = services.GetRequiredService<IConfiguration>();
            _commands = services.GetRequiredService<CommandService>();
            _client = services.GetRequiredService<DiscordSocketClient>();
            _logger = services.GetRequiredService<ILogger<CommandHandler>>();
            _prefix = services.GetRequiredService<IPrefixService>();
            _services = services;

            // take action when we execute a command
            _commands.CommandExecuted += CommandExecutedAsync;

            // take action when we receive a message (so we can process it, and see if it is a valid command)
            _client.MessageReceived += MessageReceivedAsync;
        }

        public async Task InitializeAsync()
        {
            // register modules that are public and inherit ModuleBase<T>.
            await _commands.AddModulesAsync(Assembly.GetEntryAssembly(), _services);
        }

        // this class is where the magic starts, and takes actions upon receiving messages
        public async Task MessageReceivedAsync(SocketMessage rawMessage)
        {
            // ensures we don't process system/other bot messages
            if (!(rawMessage is SocketUserMessage message))
            {
                return;
            }

            if (message.Source != MessageSource.User)
            {
                return;
            }

            IGuild guild = null;
            if (rawMessage.Author is IGuildUser guildUser)
            {
                guild = guildUser.Guild;
            }

            // sets the argument position away from the prefix we set
            var argPos = 0;

            // get prefix from the configuration file
            char prefix = Char.Parse(await _prefix.GetPrefix(guild));

            // get the role that has our name as prefix
            var role = guild?.Roles.Where(r => r.Name == _client.CurrentUser.Username).FirstOrDefault();

            // determine if the message has a valid prefix, and adjust argPos based on prefix
            if (!(message.HasMentionPrefix(_client.CurrentUser, ref argPos) || message.HasCharPrefix(prefix, ref argPos) || HasRolePrefix(message, role, ref argPos)))
            {
                if ((message.MentionedUsers.Count == 1 && message.MentionedUsers.Single().Id == _client.CurrentUser.Id)
                    || (message.MentionedRoles.Count == 1 && message.MentionedRoles.Single().Id == role?.Id))
                {
                    await rawMessage.Channel.SendMessageAsync($"Hi! I'm {_client.CurrentUser.Mention}, your friendly neighborhood bot. To interact with me, use \"{_client.CurrentUser.Mention} help\" or \"{prefix}help\". Don't worry if my prefix conflicts with another bot, you can change it to your liking!");
                }
                return;
            }

            var context = new SocketCommandContext(_client, message);

            // execute command if one is found that matches
            _commands.ExecuteAsync(context, argPos, _services).Forget(ex =>
            {
                _logger.LogWarning(ex, $"Error executing command [{message}]");
            });
        }

        private bool HasRolePrefix(IUserMessage msg, IRole role, ref int argPos)
        {
            if (role == null)
                return false;

            if (msg.Content.StartsWith(role.Mention + " "))
            {
                argPos = role.Mention.Length + 1;
                return true;
            }
            return false;
        }

        public async Task CommandExecutedAsync(Optional<CommandInfo> command, ICommandContext context, IResult result)
        {
            // if a command isn't found, log that info to console and exit this method
            if (!command.IsSpecified)
            {
                _logger.LogError($"Command failed to execute for [{context.User.Username}] <-> [{result.ErrorReason}]!");
                return;
            }


            // log success to the console and exit this method
            if (result.IsSuccess)
            {
                _logger.LogInformation($"Command [{command.Value.Name}] executed for [{context.User.Username}] on [{context.Guild.Name}]");
                return;
            }

            // failure scenario, let's let the user know
            await context.Channel.SendMessageAsync($"Sorry, {context.User.Mention}... something went wrong -> [{result}]!");
        }
    }
}
