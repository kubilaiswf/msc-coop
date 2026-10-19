using MSCCoop.Net;
using UnityEngine;

namespace MSCCoop;

// The walkers' rig has a single bone for all fingers, so no hand shape (a raised thumb or a middle
// finger) can be posed on it. The arm movements come from other NPCs on the same rig instead: the
// game animates an NPC's arm from an Animation on that arm's collar bone, or both arms from the upper
// spine, and a clip taken from there plays the same way on the same bone of the avatar.
sealed class AvatarGestures
{
    const float Fade = 0.15f;
    const string Spine = "pelvis/spine_middle/spine_upper";
    const string RightArm = Spine + "/collar_right";
    const string LeftArm = Spine + "/collar_left";

    sealed class Move
    {
        public Animation Target;
        public string On;
        public string Off;
        public string Idle;
        public bool Hold;
    }

    readonly Move[] moves = new Move[(int)Gesture.Push + 1];
    Gesture current;

    public bool Has(Gesture gesture) => moves[(int)gesture] != null;

    public static AvatarGestures Create(Transform skeleton)
    {
        var gestures = new AvatarGestures();
        // An old man shaking his fist from a window is the nearest the game has to an insult.
        gestures.Add(Gesture.Finger, skeleton, LeftArm, "PERAJARVI/HouseRintamaCrazy/Watcher/Oldman/Man/skeleton", "oldman_fist", null, false);
        gestures.Add(Gesture.Hitchhike, skeleton, RightArm, "DRAGRACE/LOD/DRAGSTRIP/Sender/Pivot/Char/skeleton", "drag_worker_pointright_on", "drag_worker_pointright_off", true);
        gestures.Add(Gesture.Hit, skeleton, RightArm, "DANCEHALL/Functions/FIGHTER/Fighter/Pivot/Char/skeleton", "fighter_right_hit_jaw", null, false);
        gestures.Add(Gesture.Push, skeleton, Spine, "DANCEHALL/Functions/GUARD/Guard/Pivot/Char/skeleton", "guard_hands_grab", "guard_hands_throw", true);
        return gestures;
    }

    void Add(Gesture gesture, Transform skeleton, string bone, string sourceSkeleton, string on, string off, bool hold)
    {
        var target = skeleton.Find(bone);
        var source = FindInScene(sourceSkeleton + "/" + bone);
        var sourceAnimation = source == null ? null : source.GetComponent<Animation>();
        if (target == null || sourceAnimation == null || sourceAnimation[on] == null || off != null && sourceAnimation[off] == null)
            return;
        var animation = target.GetComponent<Animation>();
        if (animation == null)
            animation = target.gameObject.AddComponent<Animation>();
        var idle = animation.clip == null ? null : animation.clip.name;
        animation.AddClip(sourceAnimation[on].clip, on);
        animation[on].wrapMode = hold ? WrapMode.ClampForever : WrapMode.Once;
        if (off != null)
        {
            animation.AddClip(sourceAnimation[off].clip, off);
            animation[off].wrapMode = WrapMode.Once;
        }
        moves[(int)gesture] = new Move { Target = animation, On = on, Off = off, Idle = idle, Hold = hold };
    }

    public void Set(Gesture gesture)
    {
        if (gesture == current)
            return;
        var previous = moves[(int)current];
        if (previous != null && previous.Hold)
            End(previous);
        current = gesture;
        var next = moves[(int)gesture];
        if (next == null)
            return;
        next.Target.CrossFade(next.On, Fade);
        if (!next.Hold)
            ReturnToIdle(next);
    }

    static void End(Move move)
    {
        if (move.Off != null)
            move.Target.CrossFade(move.Off, Fade);
        ReturnToIdle(move);
    }

    static void ReturnToIdle(Move move)
    {
        if (move.Idle != null)
            move.Target.CrossFadeQueued(move.Idle, Fade, QueueMode.CompleteOthers);
    }

    // NPCs that only appear for events (the drag race, the window watcher) are switched off, and
    // GameObject.Find does not see inactive objects.
    static Transform FindInScene(string path)
    {
        var slash = path.IndexOf('/');
        var rootName = path.Substring(0, slash);
        foreach (Transform t in Resources.FindObjectsOfTypeAll(typeof(Transform)))
        {
            if (t.parent != null || t.name != rootName || t.gameObject.hideFlags != HideFlags.None)
                continue;
            var found = t.Find(path.Substring(slash + 1));
            if (found != null)
                return found;
        }
        return null;
    }
}
