using HutongGames.PlayMaker;
using MSCCoop.Net;
using MSCLoader;
using UnityEngine;

namespace MSCCoop;

sealed class PlayerSync(ObjectRegistry registry, PassengerSeat passenger)
{
    const double SendInterval = 0.05;
    const float CrouchDrop = 0.35f;

    // The game's gestures run in PLAYER's PlayerFunctions FSM. Hitchhiking and pushing stay in a state
    // until the key is pressed again; the middle finger and a punch pass through their state in one
    // frame and leave an animation playing, so those are held for the length of that animation.
    const string FunctionsPath = "PLAYER/Pivot/AnimPivot/Camera/FPSCamera";
    const float FallbackMoment = 0.5f;

    readonly SnapshotBuffer[] buffers = new SnapshotBuffer[Protocol.MaxPlayers];
    readonly Avatar[] avatars = new Avatar[Protocol.MaxPlayers];
    readonly PlayerFlags[] flags = new PlayerFlags[Protocol.MaxPlayers];
    readonly uint[] frames = new uint[Protocol.MaxPlayers];
    readonly float[] pitches = new float[Protocol.MaxPlayers];
    readonly Gesture[] gestures = new Gesture[Protocol.MaxPlayers];
    Session session;
    Transform player;
    CharacterController controller;
    FsmGameObject pov;
    FsmString currentVehicle;
    FsmBool inMenu;
    PlayMakerFSM functions;
    PlayMakerFSM urinate;
    GameObject smoking;
    Gesture moment;
    float momentUntil;
    float standingEyes;
    Vector3 lastPosition;
    uint lastFrame;
    double lastSendTime = double.NaN;
    double nextSend;

    public void Init()
    {
        for (var i = 0; i < buffers.Length; i++)
            buffers[i] = new SnapshotBuffer();
    }

    public void Attach(Session newSession)
    {
        session = newSession;
        session.MessageReceived += OnMessage;
        session.PlayerLeft += p => Remove(p.Slot);
    }

    public void Detach()
    {
        session = null;
        RemoveAll();
    }

    public void OnLevelLoaded()
    {
        RemoveAll();
        player = null;
    }

    // PLAYER stays inactive until the level has finished its own loading, so it is looked up from PostLoad.
    public void OnGameReady()
    {
        var go = GameObject.Find("PLAYER");
        if (go == null)
            return;
        player = go.transform;
        controller = go.GetComponent<CharacterController>();
        standingEyes = 0;
        var globals = PlayMakerGlobals.Instance.Variables;
        pov = globals.FindFsmGameObject("POV");
        currentVehicle = globals.FindFsmString("PlayerCurrentVehicle");
        inMenu = globals.FindFsmBool("PlayerInMenu");
        moment = Gesture.None;
        HookGestures();
        lastSendTime = double.NaN;
    }

    public void Update(float deltaTime)
    {
        if (session == null || session.State != SessionState.Connected || player == null)
            return;

        var hostTime = session.HostTime;
        if (hostTime >= nextSend || nextSend - hostTime > 1)
            Send(hostTime);

        var camera = pov.Value == null ? null : pov.Value.transform;
        for (var slot = 0; slot < avatars.Length; slot++)
        {
            var avatar = avatars[slot];
            if (avatar == null)
                continue;
            var buffer = buffers[slot];
            buffer.AdvanceDelay(deltaTime);
            if (!buffer.Sample(hostTime - buffer.Delay, out var m))
                continue;
            var position = ToUnity(m.Position);
            var rotation = ToUnity(m.Rotation);
            var vehicle = frames[slot] == 0 ? null : registry.Find(frames[slot]);
            if (vehicle != null)
            {
                var frame = vehicle.Go.transform;
                position = frame.TransformPoint(position);
                rotation = frame.rotation * rotation;
            }
            avatar.Apply(position, rotation, ToUnity(m.Velocity), flags[slot], gestures[slot], camera);
        }
    }

    void Send(double hostTime)
    {
        nextSend = hostTime + SendInterval;
        // Dying replaces the player's controller with a ragdoll until the game reloads.
        if (controller == null)
            return;
        var position = player.position + controller.center - Vector3.up * (controller.height * 0.5f);
        var yaw = player.eulerAngles.y;
        var vehicle = player.parent == null ? null : registry.Find(player.root.gameObject);
        uint frame = 0;
        if (vehicle != null && vehicle.IsVehicle)
        {
            // Seats differ per vehicle and PLAYER's feet are meaningless while seated; the eyes are not.
            var t = vehicle.Go.transform;
            position = t.InverseTransformPoint(pov.Value != null ? pov.Value.transform.position : position);
            // Every seat faces forward; PLAYER keeps whatever way it faced when it got in.
            yaw = 0;
            frame = vehicle.Id;
        }

        var velocity = double.IsNaN(lastSendTime) || frame != lastFrame
            ? Vector3.zero
            : (position - lastPosition) / (float)(hostTime - lastSendTime);
        lastPosition = position;
        lastFrame = frame;
        lastSendTime = hostTime;

        var state = new PlayerState
        {
            Slot = session.LocalSlot,
            Time = hostTime,
            Position = ToNet(position),
            Velocity = ToNet(velocity),
            Yaw = yaw,
            Pitch = Pitch(),
            Flags = LocalFlags(),
            Gesture = LocalGesture(frame != 0 || passenger.Seated),
            Vehicle = frame,
        };
        state.Write(session.BeginMessage(MessageType.PlayerState));
        session.Broadcast(Channel.State);
    }

    void OnMessage(Connection from, MessageType type, BitReader reader)
    {
        if (type != MessageType.PlayerState)
            return;
        var state = PlayerState.Read(reader);
        if (reader.Overflow || state.Slot == session.LocalSlot)
            return;

        if (session.IsHost)
        {
            if (state.Slot != from.Slot)
                return;
            state.Write(session.BeginMessage(MessageType.PlayerState));
            session.Broadcast(Channel.State, from);
        }

        var info = session.Players[state.Slot];
        if (player == null || info == null)
            return;
        if (avatars[state.Slot] == null || frames[state.Slot] != state.Vehicle)
        {
            if (avatars[state.Slot] == null)
                avatars[state.Slot] = Avatar.Create(state.Slot, info.Name);
            buffers[state.Slot].Clear();
            frames[state.Slot] = state.Vehicle;
        }
        buffers[state.Slot].Add(state.Time, session.HostTime, new MotionState
        {
            Position = state.Position,
            Velocity = state.Velocity,
            Rotation = PlayerState.YawRotation(state.Yaw),
        });
        flags[state.Slot] = state.Flags;
        gestures[state.Slot] = state.Gesture;
        pitches[state.Slot] = state.Pitch;
    }

    public void LateUpdate(float deltaTime)
    {
        for (var slot = 0; slot < avatars.Length; slot++)
            avatars[slot]?.LateUpdate(pitches[slot], deltaTime);
    }

    // Taken from where the camera looks, not from one transform's angles: which object in the
    // player's hierarchy carries the vertical look is an implementation detail of the game.
    float Pitch()
    {
        if (pov.Value == null)
            return 0;
        var forward = pov.Value.transform.forward;
        return -Mathf.Asin(Mathf.Clamp(forward.y, -1f, 1f)) * Mathf.Rad2Deg;
    }

    void HookGestures()
    {
        var camera = GameObject.Find(FunctionsPath);
        functions = camera == null ? null : camera.GetPlayMaker("PlayerFunctions");
        if (functions == null)
            return;
        var piss = camera.transform.Find("Piss");
        urinate = piss == null ? null : piss.gameObject.GetPlayMaker("Logic");
        var cigarette = camera.transform.Find("FPSCamera/Smoking");
        smoking = cigarette == null ? null : cigarette.gameObject;
        var finger = ClipLength(camera.transform, "FPSCamera/MiddleFinger/Pivot", "middlefinger");
        var fist = ClipLength(camera.transform, "FPSCamera/Fist/Pivot", "fist_throw");
        functions.FsmInject("Finger", () => Moment(Gesture.Finger, finger), index: 0);
        functions.FsmInject("Fist", () => Moment(Gesture.Hit, fist), index: 0);
    }

    static float ClipLength(Transform root, string path, string clip)
    {
        var target = root.Find(path);
        var animation = target == null ? null : target.GetComponent<Animation>();
        var state = animation == null ? null : animation[clip];
        return state == null ? FallbackMoment : state.length;
    }

    void Moment(Gesture gesture, float length)
    {
        moment = gesture;
        momentUntil = Time.time + length;
    }

    Gesture LocalGesture(bool seated)
    {
        if (inMenu != null && inMenu.Value)
            return Gesture.None;
        if (Time.time < momentUntil && (!seated || moment == Gesture.Finger))
            return moment;
        if (!seated && functions != null)
        {
            switch (functions.ActiveStateName)
            {
                case "Finger 2":
                case "Thumb Up":
                    return Gesture.Hitchhike;
                case "On":
                    return Gesture.Push;
            }
        }
        // The stream runs in these states of the game's Piss logic; the cigarette is out while
        // its Smoking object is active.
        if (urinate != null)
        {
            var state = urinate.ActiveStateName;
            if (state == "Full power" || state == "Pump" || state == "State 4")
                return Gesture.Urinate;
        }
        return smoking != null && smoking.activeSelf ? Gesture.Smoke : Gesture.None;
    }

    PlayerFlags LocalFlags()
    {
        if (!string.IsNullOrEmpty(currentVehicle.Value) || passenger.Seated)
            return PlayerFlags.InVehicle;
        if (pov.Value == null)
            return PlayerFlags.None;
        var feet = player.position.y + controller.center.y - controller.height * 0.5f;
        var eyes = pov.Value.transform.position.y - feet;
        standingEyes = Mathf.Max(standingEyes, eyes);
        return eyes < standingEyes - CrouchDrop ? PlayerFlags.Crouching : PlayerFlags.None;
    }

    void Remove(byte slot)
    {
        if (slot >= avatars.Length || avatars[slot] == null)
            return;
        avatars[slot].Destroy();
        avatars[slot] = null;
        gestures[slot] = Gesture.None;
        buffers[slot].Clear();
    }

    void RemoveAll()
    {
        for (byte i = 0; i < avatars.Length; i++)
            Remove(i);
    }

    static Vec3 ToNet(Vector3 v) => new(v.x, v.y, v.z);
    static Vector3 ToUnity(Vec3 v) => new(v.X, v.Y, v.Z);
    static Quaternion ToUnity(Quat q) => new(q.X, q.Y, q.Z, q.W);
}
