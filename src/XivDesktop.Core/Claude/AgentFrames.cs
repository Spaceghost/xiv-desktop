namespace XivDesktop.Core.Claude;

/// <summary>
/// The ghostty-agent wire frames XivDesktop needs. Every frame is <c>u8 type, u32 little-endian payload
/// length, payload</c>; ids are u32 little-endian and strings are UTF-8 without NUL.
/// </summary>
/// <remarks>
/// Types 1-28 are ghostty-agent's protocol versions 1-3; only the handful XivDesktop sends or reads are
/// named here. The <c>J*</c> frames are version 4's "jobs" capability — a process on pipes rather than a
/// pseudo console, for a program whose output is parsed rather than drawn — as ghostty-dalamud's
/// <c>docs/JOBS.md</c> and <c>agent/jobs.nelua</c> define them. Job frames are sent only to an agent that
/// greeted with <see cref="JobsVersion"/> or later.
/// </remarks>
public static class AgentFrames
{
    // Version 1-3, client -> agent.
    public const byte Hello = 1;

    // Version 1-3, agent -> client.
    public const byte Ok = 16;
    public const byte Err = 17;

    /// <summary>The first protocol version that carries the job frames.</summary>
    public const int JobsVersion = 4;

    // Version 4, client -> agent.

    /// <summary>req u32, flags u8, argv entries NUL separated, an empty entry, then env entries.</summary>
    public const byte JOpen = 29;

    /// <summary>jid u32, bytes for the job's stdin. No bytes: close stdin, which ends a turn.</summary>
    public const byte JData = 30;

    /// <summary>jid u32: kill the job (SIGKILL) and stop keeping it.</summary>
    public const byte JClose = 31;

    /// <summary>jid u32, signal u8: sent to the job's process group.</summary>
    public const byte JSig = 32;

    /// <summary>jid u32: take a kept job over on this connection, with its replay.</summary>
    public const byte JAttach = 33;

    // Version 4, agent -> client.

    /// <summary>req u32, jid u32, pid u32; jid 0 means the open failed and the reason follows.</summary>
    public const byte JOpened = 34;

    /// <summary>jid u32, stream u8 (<see cref="StreamStdout"/> or <see cref="StreamStderr"/>), bytes.</summary>
    public const byte JOut = 35;

    /// <summary>jid u32, status i32 (128 + signal when killed; <see cref="NoJob"/> for an unknown jid).</summary>
    public const byte JExit = 36;

    public const byte StreamStdout = 1;
    public const byte StreamStderr = 2;

    /// <summary>JOPEN flag bit 0: the job outlives the connection that opened it.</summary>
    public const byte FlagKeep = 1;

    public const byte SigInt = 2;
    public const byte SigTerm = 15;

    /// <summary>JEXIT status for a jid the agent does not have.</summary>
    public const int NoJob = -1;

    /// <summary>
    /// The env entry that sets a job's working directory instead of an environment variable, as it does
    /// for a PTY session.
    /// </summary>
    public const string CwdEnv = "PWD=";

    public const int HeaderSize = 5;

    /// <summary>The agent's own limit; a larger frame is a protocol error on both sides.</summary>
    public const int MaxPayload = 4 * 1024 * 1024;

    /// <summary>How much of a kept job's output the agent replays on JATTACH.</summary>
    public const int ReplayBytes = 256 * 1024;
}
