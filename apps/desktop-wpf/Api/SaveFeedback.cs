namespace NovelGm.Desktop.Api;

// Per editor context: queued writes and edits made during IO must both settle before Saved.
internal sealed class SaveFeedback
{
    internal int Pending { get; private set; }
    internal bool Dirty { get; set; }
    internal bool Failed { get; private set; }
    internal string Key => Failed ? "Save.StateFailed" : Dirty ? "Save.StatePending"
        : Pending > 0 ? "Save.StateSaving" : "Save.StateSaved";

    internal void Queue() { Pending++; Failed = false; }
    internal void Complete(bool success)
    {
        Pending--;
        if (!success) Failed = true;
        else if (Pending == 0 && !Dirty) Failed = false;
    }
}
