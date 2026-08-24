/// <summary>
/// One line of live progress for a terminal, and silence for everything else.
///
/// A pipe, a log file or an ai agent reading the output gets clean event lines and the final
/// summary - a rewriting progress line would land there as garbage, so it only exists when the
/// output really is a terminal.
/// </summary>
public class ProgressLine
{
    /// <summary>a human is watching; a redirected output never sees the rewriting line</summary>
    public static bool IsLive => !Console.IsOutputRedirected;

    private readonly object _lock = new();
    private int _lastLength;

    public void Update(string text)
    {
        if (!IsLive)
            return;

        lock (_lock)
        {
            Console.Write($"\r{text.PadRight(_lastLength)}");
            _lastLength = text.Length;
        }
    }

    /// <summary>wipes the progress line so a normal line can take its place</summary>
    public void Clear()
    {
        if (!IsLive)
            return;

        lock (_lock)
        {
            Console.Write($"\r{new string(' ', _lastLength)}\r");
            _lastLength = 0;
        }
    }
}
