namespace Flock.Models.CustomModels
{
    // One queued offline write, replayed  to  the same endpoint when the server is reachable again.
    public class PendingDataWrite
    {
        public string Path { get; set; }        // versioned-API-relative endpoint, e.g. game_command/update_player_data
        public string PayloadJson { get; set; } // serialized request body, re-POSTed as-is
        public string Context { get; set; }     // log label for the replay
    }
}
