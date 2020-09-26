using Betty.Bot.Services;
using Discord;
using Discord.Commands;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Betty.Bot.Modules
{
    [Name("Help")]
    public class HelpModule : ModuleBase<SocketCommandContext>
    {
        private readonly CommandService _service;
        private readonly IConfiguration _config;
        private readonly IPrefixService _prefix;

        public HelpModule(CommandService service, IConfiguration config, IPrefixService prefix)
        {
            _service = service;
            _config = config;
            _prefix = prefix;
        }

        [Command("help")]
        public async Task HelpAsync()
        {
            string prefix = await _prefix.GetPrefix(Context.Guild);
            var builder = new EmbedBuilder()
            {
                Color = new Color(114, 137, 218),
                Description = "These are the commands you can use. Note that most commands can be used *without arguments*, the bot will ask for them, often providing examples or other details."
            };

            foreach (var module in _service.Modules)
            {
                string description = string.Empty;

                if (!string.IsNullOrEmpty(module.Summary))
                {
                    var summary = module.Summary.Replace("{prefix}", prefix);
                    description += $"*{summary}*\n";
                }

                foreach (var cmd in module.Commands)
                {
                    var result = await cmd.CheckPreconditionsAsync(Context);
                    var access = result.IsSuccess ? "✅" : "⛔";
                    var args = cmd.Parameters.Count == 0 ? "" : string.Join(" ", cmd.Parameters.Select(p => p.IsOptional ? $"[{p.Name}]" : $"<{p.Name}>"));
                    description += $"{access} {prefix}{cmd.Aliases.First()} {args}\n";
                }

                if (!string.IsNullOrWhiteSpace(description))
                {
                    builder.AddField(x =>
                    {
                        x.Name = module.Name;
                        x.Value = description;
                        x.IsInline = false;
                    });
                }
            }

            await ReplyAsync("", false, builder.Build());
        }

        [Command("help")]
        public async Task HelpAsync([Remainder] string command)
        {
            string prefix = await _prefix.GetPrefix(Context.Guild);

            if (command.StartsWith(prefix))
                command = command.Substring(prefix.Length);

            var result = _service.Search(Context, command);

            if (!result.IsSuccess)
            {
                await ReplyAsync($"Sorry, I couldn't find a command like **{command}**.");
                return;
            }

            var builder = new EmbedBuilder()
            {
                Color = new Color(114, 137, 218),
                Description = $"Here are some commands like **{command}**"
            };

            foreach (var match in result.Commands)
            {
                var cmd = match.Command;

                builder.AddField(x =>
                {
                    x.Name = string.Join(", ", cmd.Aliases);
                    x.Value = $"Parameters: {string.Join(", ", cmd.Parameters.Select(p => p.Name))}\n" +
                              $"Summary: {cmd.Summary}";
                    x.IsInline = false;
                });
            }

            await ReplyAsync("", false, builder.Build());
        }

        [Command("changelog")]
        public async Task ChangelogAsync()
        {
            var changelog = await new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("Betty.Bot.gitlog.txt")).ReadToEndAsync();
            await ReplyAsync($"A bit technical, but here's the recent list of changes: ```{changelog}```");
        }
    }
}
