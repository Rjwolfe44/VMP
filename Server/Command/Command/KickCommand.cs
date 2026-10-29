using Server.Client;
using Server.Command.Command.Base;
using Server.Command.Common;
using Server.Log;
using Server.Server;

namespace Server.Command.Command
{
    public class KickCommand : SimpleCommand
    {
        public override bool Execute(string commandArgs)
        {
            CommandSystemHelperMethods.SplitCommand(commandArgs, out var playerName, out var reason);
            reason = string.IsNullOrEmpty(reason) ? "No reason specified" : reason;

            if (playerName != "")
            {
                return KickPlayer(playerName, reason);
            }

            LunaLog.Error("Syntax error. Usage: /kick playername [reason]");
            return false;
        }

        /// <summary>Kicks a connected player by name. Shared by the console command and the admin API.</summary>
        public static bool KickPlayer(string playerName, string reason)
        {
            reason = string.IsNullOrEmpty(reason) ? "No reason specified" : reason;
            var player = ClientRetriever.GetClientByName(playerName);
            if (player != null)
            {
                LunaLog.Normal($"Kicking {playerName} from the server");
                MessageQueuer.SendConnectionEnd(player, $"Kicked from the server: {reason}");
                return true;
            }

            LunaLog.Normal($"Player: {playerName} not found");
            return false;
        }
    }
}
