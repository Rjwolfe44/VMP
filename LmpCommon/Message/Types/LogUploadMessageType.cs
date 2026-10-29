namespace LmpCommon.Message.Types
{
    public enum LogUploadMessageType
    {
        // Client -> server
        Begin = 0,
        Chunk = 1,
        Cancel = 2,

        // Server -> client
        Accepted = 3,
        Done = 4,
        Refused = 5,

        // Server -> client, added later: values are on the wire and are never renumbered, new ones go at the end.
        /// <summary>The host asks the player's game to send a diagnostics bundle (<see cref="Data.LogUpload.LogUploadRequestMsgData"/>).</summary>
        Request = 6,
    }
}
