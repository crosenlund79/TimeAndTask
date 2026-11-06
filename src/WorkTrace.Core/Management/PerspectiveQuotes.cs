namespace WorkTrace.Core.Management;

/// <summary>
/// Short, original lines shown above the task list to take the edge off a long list: one thing at a time, small
/// steps, progress over perfection. Stored locally; no attributions.
/// </summary>
public static class PerspectiveQuotes
{
    public static IReadOnlyList<string> All { get; } =
    [
        "An elephant is eaten one bite at a time.",
        "Seventeen tasks. One pair of hands. Choose one.",
        "The whole mountain isn't today's assignment. The next step is.",
        "A small finished task beats a magnificent unfinished plan.",
        "You don't have to untangle the entire knot. Find one loose end.",
        "Your task list is a menu, not a court summons.",
        "One thing done is better than seventeen things worried about.",
        "You are allowed to finish one thing before thinking about the other sixteen.",
        "Big tasks are just small tasks standing very close together.",
        "If it feels too big to start, it is too big. Cut it in half and try again.",
        "Write the first sentence. The second one is usually easier.",
        "Nobody has ever read a to-do list into completion.",
        "Done and imperfect still counts as done.",
        "Good enough, finished, outranks perfect, pending.",
        "The draft is allowed to be bad. That is its whole job.",
        "Ready is a feeling that usually arrives about ten minutes after you start.",
        "Start before you feel prepared. Preparation tends to show up once you are moving.",
        "You do not need the whole route. You need the next turn.",
        "Today only has to hold today's work, not the whole quarter.",
        "Leaving something for tomorrow is planning, not failure.",
        "Not everything urgent is yours, and not everything yours is urgent.",
        "What can you actually move forward in the next twenty minutes?",
        "Worrying about the backlog has, so far, completed none of it.",
        "The list will still be long tomorrow. That is what lists do.",
        "A backlog is a library, not a debt collector.",
        "Pick the task you can finish, not the one that judges you the most.",
        "One open tab in your head is plenty.",
        "Progress is quieter than panic, but it gets more done.",
        "Momentum is built from small, boring completions.",
        "If you can't do all of it, do the part you can do now.",
        "Ten minutes of doing beats an hour of rehearsing.",
        "The task does not know how long it has been on the list. Only you do.",
        "Thinking about everything at once is a reliable way to do nothing at all.",
        "Choose the next step small enough that starting feels slightly silly.",
        "You can stop wondering where to begin: begin with whatever is in front of you.",
        "Finishing one thing makes room for the next one, in your head as well as on the list.",
        "Some days the win is a short list of closed items, not a cleared desk.",
        "Overthinking is just doing, with all the useful parts removed.",
        "The perfect order to do things in is any order where something gets done.",
        "Nothing on this list is improved by being stared at.",
        "Slow is fine. Stopped is the only problem, and even that is temporary.",
        "You are not behind on everything. You are early on the next thing.",
        "Every finished task was once an unstarted one that looked a bit intimidating.",
        "The best time to rethink the whole plan is after the next small step.",
        "Rest is not the opposite of progress. It is part of the schedule.",
        "One clear priority beats five vague intentions.",
        "If everything is important, pick the one that unblocks someone else.",
        "Close one loop. The world will notice, eventually.",
    ];

    /// <summary>The quote at a position in the rotation (wraps around).</summary>
    public static string At(int index) => All[((index % All.Count) + All.Count) % All.Count];
}
