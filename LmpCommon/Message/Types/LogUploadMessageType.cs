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
    }
}
