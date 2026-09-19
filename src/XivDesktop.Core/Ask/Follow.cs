namespace XivDesktop.Core.Ask;

/// <summary>Which locomotion loop the follower should show.</summary>
public enum Gait
{
    Idle,
    Walk,
    Run,
}

/// <summary>Tuning for <see cref="Follower"/>; the feel follows ghostty-dalamud's pet panels (lua/world.lua M.pet).</summary>
public sealed record FollowTuning
{
    /// <summary>Yalms from the player while following.</summary>
    public double Distance { get; init; } = 1.9;

    /// <summary>Radians from the player's heading to the follow slot (π/2 = square to the side).</summary>
    public double Side { get; init; } = 1.3;

    /// <summary>Spring stiffness (higher follows more tightly).</summary>
    public double Stiffness { get; init; } = 4.0;

    /// <summary>1 = critically damped, no overshoot.</summary>
    public double Damping { get; init; } = 1.0;

    /// <summary>Never nearer the player than this (yalms).</summary>
    public double MinDistance { get; init; } = 1.1;

    /// <summary>Keep this far from the line between the camera and the player (yalms).</summary>
    public double CameraClearance { get; init; } = 0.9;

    /// <summary>Player speed (yalms/s) above which the player counts as moving.</summary>
    public double MovingSpeed { get; init; } = 0.6;

    /// <summary>The conversation spot in front of the player is left once the player is this far away.</summary>
    public double LeaveConversation { get; init; } = 3.2;

    /// <summary>Follower speed thresholds for the walk and run loops (yalms/s).</summary>
    public double WalkAbove { get; init; } = 0.45;

    public double RunAbove { get; init; } = 3.8;

    /// <summary>Radians per second the follower turns.</summary>
    public double TurnSpeed { get; init; } = 7.0;
}

/// <summary>
/// Client-side follow behaviour for the summoned speaker, in the game's ground plane: x and z, yaw with
/// forward = (sin yaw, cos yaw). It appears in front of the player facing them; once the player walks
/// away it trails beside them on a critically damped spring (the side away from the camera line, never
/// behind them), picks walk/run/idle from its own speed, and turns to face the player when they stop or
/// while talking. Pure math: the plugin feeds positions each frame and writes the result to the local
/// object.
/// </summary>
public sealed class Follower
{
    private double vx;
    private double vz;
    private double playerSpeed;
    private double lastPx;
    private double lastPz;
    private bool havePlayer;
    private int sideSign = 1;

    public Follower(FollowTuning? tuning = null)
    {
        Tuning = tuning ?? new FollowTuning();
    }

    public FollowTuning Tuning { get; }

    public double X { get; private set; }

    public double Y { get; private set; }

    public double Z { get; private set; }

    public double Yaw { get; private set; }

    /// <summary>The follower's own ground speed (yalms/s), smoothed.</summary>
    public double Speed { get; private set; }

    /// <summary>The player's ground speed (yalms/s), smoothed.</summary>
    public double PlayerSpeed => playerSpeed;

    /// <summary>Standing at the conversation spot in front of the player (not following yet).</summary>
    public bool Conversing { get; private set; } = true;

    public Gait Gait => Speed > Tuning.RunAbove ? Gait.Run : Speed > Tuning.WalkAbove ? Gait.Walk : Gait.Idle;

    /// <summary>Places the follower <paramref name="distance"/> yalms in front of the player, facing them.</summary>
    public void Spawn(double px, double py, double pz, double playerYaw, double distance = 2.0)
    {
        X = px + Math.Sin(playerYaw) * distance;
        Z = pz + Math.Cos(playerYaw) * distance;
        Y = py;
        Yaw = Wrap(playerYaw + Math.PI);
        vx = vz = 0;
        Speed = 0;
        Conversing = true;
        lastPx = px;
        lastPz = pz;
        playerSpeed = 0;
        havePlayer = true;
    }

    /// <summary>
    /// One frame. <paramref name="talking"/> keeps the follower facing the player; the camera position
    /// (x, z) steers it off the camera line, or pass NaN when unknown.
    /// </summary>
    public void Update(double dt, double px, double py, double pz, double playerYaw, bool talking, double camX = double.NaN, double camZ = double.NaN)
    {
        dt = Math.Clamp(dt, 0, 0.1);
        if (!havePlayer)
        {
            Spawn(px, py, pz, playerYaw);
            return;
        }

        if (dt > 0)
        {
            var pv = Math.Sqrt(Sq(px - lastPx) + Sq(pz - lastPz)) / dt;
            playerSpeed += (pv - playerSpeed) * (1 - Math.Exp(-dt * 8));
        }

        lastPx = px;
        lastPz = pz;
        var moving = playerSpeed > Tuning.MovingSpeed;
        var dist = Math.Sqrt(Sq(X - px) + Sq(Z - pz));
        if (Conversing && (dist > Tuning.LeaveConversation || (moving && !talking && dist > Tuning.Distance + 0.6)))
            Conversing = false;

        double tx, tz;
        if (Conversing)
        {
            tx = X;
            tz = Z;
        }
        else
        {
            (tx, tz) = Slot(px, pz, playerYaw, camX, camZ);
            if (!moving && dist <= Tuning.Distance + 0.4)
            {
                // The player stopped and we are close: stay put rather than orbit into the exact slot.
                tx = X;
                tz = Z;
            }
        }

        var ox = X;
        var oz = Z;
        (X, vx) = Spring(X, vx, tx, dt);
        (Z, vz) = Spring(Z, vz, tz, dt);
        Y += (py - Y) * (1 - Math.Exp(-dt * 10));

        // Hard guarantees on top of the spring: out of the player's personal space and off the camera line.
        (X, Z) = KeepOut(X, Z, px, pz, Tuning.MinDistance);
        if (!Conversing && !double.IsNaN(camX))
            (X, Z) = ClearOfSegment(X, Z, camX, camZ, px, pz, Tuning.CameraClearance);

        if (dt > 0)
        {
            var v = Math.Sqrt(Sq(X - ox) + Sq(Z - oz)) / dt;
            Speed += (v - Speed) * (1 - Math.Exp(-dt * 10));
        }

        // Face where we are going while moving, else the player.
        var want = Gait != Gait.Idle && !talking ? Math.Atan2(X - ox, Z - oz) : YawTowards(X, Z, px, pz);
        Yaw = TurnToward(Yaw, want, Tuning.TurnSpeed * dt);
    }

    /// <summary>The follow slot: beside the player, on the side away from the camera line, never behind.</summary>
    public (double X, double Z) Slot(double px, double pz, double heading, double camX, double camZ)
    {
        if (!double.IsNaN(camX))
        {
            // Camera to the player's right (in the player's frame) → stand on the left, and vice versa.
            var rx = Math.Cos(heading);
            var rz = -Math.Sin(heading);
            var side = (camX - px) * rx + (camZ - pz) * rz;
            if (Math.Abs(side) > 0.5)
                sideSign = side > 0 ? -1 : 1;
        }

        var ang = heading + sideSign * Tuning.Side;
        return (px + Math.Sin(ang) * Tuning.Distance, pz + Math.Cos(ang) * Tuning.Distance);
    }

    private (double X, double V) Spring(double x, double v, double target, double dt)
    {
        if (dt <= 0)
            return (x, v);
        var w = Math.Sqrt(Tuning.Stiffness) * 2;
        var steps = Math.Max(1, (int)Math.Ceiling(dt / (1.0 / 120)));
        var h = dt / steps;
        for (var i = 0; i < steps; i++)
        {
            var acc = w * w * (target - x) - 2 * Tuning.Damping * w * v;
            v += acc * h;
            x += v * h;
        }

        return (x, v);
    }

    public static (double X, double Z) KeepOut(double x, double z, double cx, double cz, double min)
    {
        var dx = x - cx;
        var dz = z - cz;
        var r = Math.Sqrt(dx * dx + dz * dz);
        if (r >= min)
            return (x, z);
        if (r < 1e-6)
        {
            dx = 1;
            dz = 0;
            r = 1;
        }

        return (cx + dx / r * min, cz + dz / r * min);
    }

    /// <summary>Pushes (x, z) out to <paramref name="clearance"/> from the segment a→b (sideways only).</summary>
    public static (double X, double Z) ClearOfSegment(double x, double z, double ax, double az, double bx, double bz, double clearance)
    {
        var sx = bx - ax;
        var sz = bz - az;
        var len2 = sx * sx + sz * sz;
        if (len2 < 1e-9)
            return (x, z);
        var t = ((x - ax) * sx + (z - az) * sz) / len2;
        if (t <= 0 || t >= 1)
            return (x, z); // beside the camera or beyond the player: not on the line
        var qx = ax + sx * t;
        var qz = az + sz * t;
        var dx = x - qx;
        var dz = z - qz;
        var d = Math.Sqrt(dx * dx + dz * dz);
        if (d >= clearance)
            return (x, z);
        if (d < 1e-6)
        {
            // Exactly on the line: step to the segment's left.
            var l = Math.Sqrt(len2);
            dx = -sz / l;
            dz = sx / l;
            d = 1;
        }

        return (qx + dx / d * clearance, qz + dz / d * clearance);
    }

    /// <summary>The yaw that looks from (x, z) toward (tx, tz).</summary>
    public static double YawTowards(double x, double z, double tx, double tz) => Math.Atan2(tx - x, tz - z);

    /// <summary>Turns <paramref name="current"/> toward <paramref name="target"/> by at most <paramref name="maxStep"/> radians.</summary>
    public static double TurnToward(double current, double target, double maxStep)
    {
        var diff = Wrap(target - current);
        if (Math.Abs(diff) <= maxStep)
            return Wrap(target);
        return Wrap(current + Math.Sign(diff) * maxStep);
    }

    public static double Wrap(double a)
    {
        a = (a + Math.PI) % (2 * Math.PI);
        if (a < 0)
            a += 2 * Math.PI;
        return a - Math.PI;
    }

    private static double Sq(double v) => v * v;
}
