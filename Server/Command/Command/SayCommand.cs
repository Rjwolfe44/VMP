using LmpCommon.Message.Data.Chat;
using LmpCommon.Message.Server;
using Server.Command.Command.Base;
using Server.Context;
using Server.Log;
using Server.Server;
using Server.Settings.Structures;

namespace Server.Command.Command
{
    public class SayCommand : SimpleCommand
    {
        public override bool Execute(string commandArgs)
        {
            Broadcast(commandArgs);
            return true;
        }

        /// <summary>Sends a chat message from the console identifier to every client. Shared by the console command and the admin API.</summary>
        public static void Broadcast(string text)
        {
            LunaLog.Normal($"Broadcasting {text}");

            var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<ChatMsgData>();
            msgData.From = GeneralSettings.SettingsStore.ConsoleIdentifier;
            msgData.Text = text;

            MessageQueuer.SendToAllClients<ChatSrvMsg>(msgData);
        }
    }
}
