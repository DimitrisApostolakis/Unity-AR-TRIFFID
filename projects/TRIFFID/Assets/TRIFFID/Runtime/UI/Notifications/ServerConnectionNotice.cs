using UnityEngine;

// Tells the user when the link to the server is lost and when it is back.
// GeoJsonApiManager reports the outcome of every request here; a card is
// shown only when the state changes, and once for the first request of a
// session, so polling never produces a stream of cards.
public static class ServerConnectionNotice
{
    private enum State { Unknown, Online, Offline }

    private static State state = State.Unknown;

    // Play sessions in the Editor can keep statics alive; start each one fresh.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Reset()
    {
        state = State.Unknown;
    }

    // reachable: the server answered, whatever the answer was. It is false
    // only when no answer arrived (no network, wrong address, timeout).
    public static void Report(bool reachable)
    {
        State next = reachable ? State.Online : State.Offline;
        if (next == state)
            return;

        state = next;
        if (reachable)
            TriffidNotification.ShowSuccess("Connected to the server.", "Online");
        else
            TriffidNotification.ShowError("No connection to the server.", "Offline");
    }
}
