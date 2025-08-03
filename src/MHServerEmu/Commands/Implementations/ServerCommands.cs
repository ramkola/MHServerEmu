using System.Text;
using Gazillion;
using MHServerEmu.Commands.Attributes;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Games.GameData.LiveTuning;

namespace MHServerEmu.Commands.Implementations
{
    [CommandGroup("server")]
    [CommandGroupDescription("Server management commands.")]
    public class ServerCommands : CommandGroup
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        [Command("status")]
        [CommandDescription("Prints server status.")]
        [CommandUsage("server status")]
        public string Status(string[] @params, NetClient client)
        {
            StringBuilder sb = new();
            sb.AppendLine("Server Status");
            sb.AppendLine(ServerApp.VersionInfo);
            sb.Append(ServerManager.Instance.GetServerStatus(client == null));
            string status = sb.ToString();

            // Display in the console as is
            if (client == null)
                return status;

            // Split for the client chat window
            CommandHelper.SendMessageSplit(client, status, false);
            return string.Empty;
        }

        [Command("broadcast")]
        [CommandDescription("Broadcasts a notification to all players.")]
        [CommandUsage("server broadcast")]
        [CommandUserLevel(AccountUserLevel.Admin)]
        [CommandParamCount(1)]
        public string Broadcast(string[] @params, NetClient client)
        {
            var groupingManager = ServerManager.Instance.GetGameService(GameServiceType.GroupingManager) as IMessageBroadcaster;
            if (groupingManager == null) return "Failed to connect to the grouping manager.";

            string message = string.Join(' ', @params);

            groupingManager.BroadcastMessage(ChatServerNotification.CreateBuilder().SetTheMessage(message).Build());
            Logger.Trace($"Broadcasting server notification: \"{message}\"");

            return string.Empty;
        }

        [Command("reloadlivetuning")]
        [CommandDescription("Reloads live tuning settings.")]
        [CommandUsage("server reloadlivetuning")]
        [CommandUserLevel(AccountUserLevel.Admin)]
        [CommandInvokerType(CommandInvokerType.ServerConsole)]
        public string ReloadLiveTuning(string[] @params, NetClient client)
        {
            LiveTuningManager.Instance.LoadLiveTuningDataFromDisk();
            return string.Empty;
        }

        [Command("shutdown")]
        [CommandDescription("Shuts the server down.")]
        [CommandUsage("server shutdown")]
        [CommandUserLevel(AccountUserLevel.Admin)]
        public string Shutdown(string[] @params, NetClient client)
        {
            string shutdownRequester = client == null ? "the server console" : client.ToString();
            Logger.Info($"Server shutdown request received from {shutdownRequester}");

            // Schedule shutdown with proper error handling
            _ = Task.Run(async () =>
            {
                try
                {
                    // Small delay to ensure command response is sent
                    await Task.Delay(500);
                    Logger.Info("Executing scheduled server shutdown...");
                    ServerApp.Instance.Shutdown();
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error during shutdown: {ex.Message}");
                }
            });

            return "Server shutdown scheduled in 0.5 seconds...";
        }
    }
}
