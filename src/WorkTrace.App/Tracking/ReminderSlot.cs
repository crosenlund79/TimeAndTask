namespace WorkTrace.App.Tracking;

/// <summary>One reminder popup at a time: the maintenance and planning reminders take the slot, the others wait for it.</summary>
public sealed class ReminderSlot
{
    public bool IsBusy { get; private set; }

    public bool TryEnter()
    {
        if (IsBusy)
        {
            return false;
        }

        IsBusy = true;
        return true;
    }

    public void Exit() => IsBusy = false;
}
