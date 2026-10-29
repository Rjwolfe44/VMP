using Server.Admin;
using Server.Command.Command.Base;
using Server.Command.Common;
using Server.Log;
using Server.System;

namespace Server.Command.Command
{
    /// <summary>Asks one connected player's game to send a diagnostics bundle. Usage: <c>requestlogs player [note]</c>.</summary>
    public class RequestLogsCommand : SimpleCommand
    {
        public override bool Execute(string commandArgs)
        {
            CommandSystemHelperMethods.SplitCommand(commandArgs ?? string.Empty, out var playerName, out var note);
            if (playerName == "")
            {
                LunaLog.Error("Syntax error. Usage: /requestlogs playername [note]");
                return false;
            }

            return RequestLogs(playerName, AdminLogTail.Clean(note).Trim());
        }

        /// <summary>Same call the admin API makes; the console prints the outcome.</summary>
        public static bool RequestLogs(string playerName, string note)
        {
            var result = LogUploadSystem.RequestLogs(playerName, note);
            if (result.Sent)
                LunaLog.Normal(result.Describe());
            else
                LunaLog.Warning(result.Describe());
            return result.Sent;
        }
    }
}
