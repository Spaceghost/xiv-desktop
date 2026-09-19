using XivDesktop.Core.Claude;

namespace XivDesktop.Plugin.Claude;

/// <summary>Why a job ended, for the panel's notice.</summary>
public sealed record JobEnded(uint Job, int Status, string Reason);

/// <summary>
/// The one seam between XivDesktop and whatever runs a Claude session. Today it is ghostty-agent's job
/// frames over TCP; the panel, the sessions and the tests never touch a socket, so a different runner
/// (a local process, a different agent) only has to implement this.
/// </summary>
public interface IJobTransport : IDisposable
{
    /// <summary>The agent is connected and greeted with a version that has the job frames.</summary>
    bool Ready { get; }

    /// <summary>Why it is not ready, for the panel's header ("" when it is).</summary>
    string Problem { get; }

    /// <summary>Raised on the transport's own thread whenever a job writes a whole line.</summary>
    event Action<uint, bool, string>? Line;

    /// <summary>Raised when a job ends, or the transport gives up on it.</summary>
    event Action<JobEnded>? Ended;

    /// <summary>Raised when the connection comes back, so live sessions can re-attach.</summary>
    event Action? Reconnected;

    /// <summary>
    /// Starts a job. Returns the request id straight away; <paramref name="opened"/> is called with the
    /// job id once the agent answers, or 0 and the reason when it refused.
    /// </summary>
    uint Open(string cwd, IReadOnlyList<string> argv, IReadOnlyList<string> env, Action<uint, string> opened);

    /// <summary>Writes one line to the job's stdin.</summary>
    void Send(uint job, string line);

    /// <summary>Closes the job's stdin, which is how a turn stream ends.</summary>
    void EndInput(uint job);

    /// <summary>Interrupts the current turn without ending the session.</summary>
    void Interrupt(uint job);

    /// <summary>Ends the job for good.</summary>
    void Close(uint job);

    /// <summary>
    /// Takes a kept job over on this connection. The agent replays its ring, so the caller must expect
    /// to see lines it has already rendered and drop them.
    /// </summary>
    void Attach(uint job);
}
