using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Betty.Bot.Extensions
{
    public static class DiscordExtensions
    {
        //public static Task<string> Summary(this SocketGuildChannel gchannel)
        //{
        //    //var items = new List<string>();
        //    //items.Add($"Name: {gchannel.Name}");
        //    //items.Add($"Position: {gchannel.Position}");
        //    //items.Add($"PermissionOverwrites: {gchannel.PermissionOverwrites.Count}");
        //    //if (gchannel is SocketTextChannel tchannel)
        //    //{
        //    //    tchannel.
        //    //}
        //    //return Task.FromResult($"Name: {gchannel.Name}, Position: {gchannel.Position}, PermissionOverwrites: {gchannel.PermissionOverwrites.Count}");
        //}

        public static Task<Dictionary<string,string>> SummarizeChanges(this SocketGuildChannel gbefore, SocketGuildChannel gafter)
        {
            var dict = new Dictionary<string,string>();

            if (gbefore.Name != gafter.Name) dict.Add("Name", $"{gbefore.Name} => {gafter.Name}");
            if (gbefore.PermissionOverwrites.Count != gafter.PermissionOverwrites.Count) dict.Add("PermissionOverwrites", $"{gbefore.PermissionOverwrites.Count} => {gafter.PermissionOverwrites.Count}");
            if (gbefore.Position != gafter.Position) dict.Add("Position", $"{gbefore.Position} => {gafter.Position}");

            if (gbefore is SocketTextChannel tbefore &&
                gafter is SocketTextChannel tafter)
            {
                if (tbefore.Category?.Name != tafter.Category?.Name) dict.Add("CategoryName", $"{tbefore.Category?.Name} => {tafter.Category?.Name}");
                if (tbefore.IsNsfw != tafter.IsNsfw) dict.Add("NSFW", $"{tbefore.IsNsfw} => {tafter.IsNsfw}");
                if (tbefore.SlowModeInterval != tafter.SlowModeInterval) dict.Add("SlowModeInterval", $"{tbefore.SlowModeInterval} => {tafter.SlowModeInterval}");
                if (tbefore.Topic != tafter.Topic) dict.Add($"Topic", $"{tbefore.Topic} => {tafter.Topic}");
            }

            if (gbefore is SocketVoiceChannel vbefore &&
                gafter is SocketVoiceChannel vafter)
            {
                if (vbefore.Category?.Name != vafter.Category?.Name) dict.Add("CategoryName", $"{vbefore.Category?.Name} => {vafter.Category?.Name}");
                if (vbefore.Bitrate != vafter.Bitrate) dict.Add("Bitrate", $"{vbefore.Bitrate} => {vafter.Bitrate}");
                if (vbefore.UserLimit != vafter.UserLimit) dict.Add("UserLimit", $"{vbefore.UserLimit} => {vafter.UserLimit}");
            }

            return Task.FromResult(dict);
        }

        public static Task<string> SummarizeName(this IUser user)
        {
            if (user is SocketGuildUser guser &&
                guser.Nickname != null)
            {
                return Task.FromResult($"{guser.Username}#{guser.Discriminator} ({guser.Nickname})");
            }
            return Task.FromResult($"{user.Username}#{user.Discriminator}");
        }

        public static Task<Dictionary<string,string>> SummarizeChanges(this IMessage mbefore, IMessage mafter)
        {
            var dict = new Dictionary<string,string>();

            if (mbefore.Content != mafter.Content)
            {
                dict.Add("Content before", mbefore.Content ?? string.Empty);
                dict.Add("Content after", mafter.Content ?? string.Empty);
            }

            if (mbefore.Flags != mafter.Flags) dict.Add("Flags", $"{mbefore.Flags} => {mafter.Flags}");
            if (mbefore.IsPinned != mafter.IsPinned) dict.Add("Pinned", $"{mbefore.IsPinned} => {mafter.IsPinned}");
            if (mbefore.IsSuppressed != mafter.IsSuppressed) dict.Add("IsSuppressed", $"{mbefore.IsSuppressed} => {mafter.IsSuppressed}");
            if (mbefore.IsTTS != mafter.IsTTS) dict.Add("IsTTS", $"{mbefore.IsTTS} => {mafter.IsTTS}");

            return Task.FromResult(dict);
        }

        public async static Task<Dictionary<string,string>> SummarizeChanges(this SocketUser ubefore, SocketUser uafter)
        {
            var dict = new Dictionary<string, string>();

            if (await ubefore.SummarizeName() != await uafter.SummarizeName()) dict.Add("Name", $"{await ubefore.SummarizeName()} => {await uafter.SummarizeName()}");
            if (ubefore.GetAvatarUrl() != uafter.GetAvatarUrl()) dict.Add("Avatar", "has changed");
            if (ubefore.PublicFlags != uafter.PublicFlags) dict.Add("PublicFlags", $"{ubefore.PublicFlags} => {uafter.PublicFlags}");

            if (ubefore is SocketGuildUser gbefore &&
                uafter is SocketGuildUser gafter)
            {
                if (gbefore.PremiumSince != gafter.PremiumSince) dict.Add("PremiumSince", $"{gbefore.PremiumSince} => {gafter.PremiumSince}");
                if (gbefore.Roles.Count != gafter.Roles.Count) dict.Add("RoleCount", $"{gbefore.Roles.Count} => {gafter.Roles.Count}");
                if (gbefore.VoiceChannel != gafter.VoiceChannel) dict.Add("VoiceChannel", $"{gbefore.VoiceChannel?.Name} => {gafter.VoiceChannel?.Name}");
            }

            return dict;
        }
    }
}
