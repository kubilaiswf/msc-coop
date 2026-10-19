using HutongGames.PlayMaker;
using MSCCoop.Net;
using MSCLoader;
using UnityEngine;

namespace MSCCoop;

// The house phone is one phone for everyone. Only the host's scheduler (PhoneLogic) picks callers;
// it announces each call with its topic and a seed, and the call FSM (Logic/Ring) runs on every
// machine. Its random choices (how many rings, which of a caller's lines) are made with that seed,
// so every machine plays the same call, and an answer or hang-up on one machine is applied to all.
sealed class PhoneSync
{
    const string LogicPath = "YARD/Building/LIVINGROOM/Telephone/Logic";
    const int MaxTopicChars = 32;

    // These calls act on whoever holds the handset: lightning can strike them in a thunderstorm,
    // and Suski's late call ends with them falling asleep. Everyone else only hears the ringing stop.
    const string ThunderState = "Thunder calls";
    static readonly string[] HandsetOnlyTopics = { "SUSKI2" };
    static readonly string[] SeededStates = { "State 3", "Order", "Stolen fuel", "Ferndale" };

    Session session;
    PlayMakerFSM scheduler;
    PlayMakerFSM jokes;
    PlayMakerFSM call;
    GameObject callObject;
    FsmString topic;
    FsmBool answer;
    FsmString subtitle;
    int seed;
    int savedSeed;
    bool answeredByNetwork;
    bool ready;

    bool Active => ready && session != null && session.State == SessionState.Connected;

    public void Attach(Session newSession)
    {
        session = newSession;
        session.MessageReceived += OnMessage;
    }

    public void Detach()
    {
        session = null;
        ready = false;
    }

    public void OnLevelLoaded() => ready = false;

    public void OnGameReady()
    {
        if (session == null)
            return;
        var logic = GameObject.Find(LogicPath);
        var ring = logic == null ? null : logic.transform.Find("Ring");
        var useHandle = logic == null ? null : logic.transform.Find("UseHandle");
        var phoneLogic = logic == null ? null : logic.transform.Find("PhoneLogic");
        if (ring == null || useHandle == null || phoneLogic == null)
        {
            ModConsole.Error("Co-op: could not find the house phone, calls will not sync.");
            return;
        }
        callObject = ring.gameObject;
        call = callObject.GetPlayMaker("Ring");
        scheduler = phoneLogic.gameObject.GetPlayMaker("Ring");
        jokes = phoneLogic.gameObject.GetPlayMaker("Jokes");
        var handle = useHandle.gameObject.GetPlayMaker("Use");
        topic = call.FsmVariables.FindFsmString("Topic");
        answer = call.FsmVariables.FindFsmBool("Answer");
        subtitle = PlayMakerGlobals.Instance.Variables.FindFsmString("GUIsubtitle");

        foreach (var state in SeededStates)
        {
            call.FsmInject(state, UseSeed, index: 0);
            call.FsmInject(state, RestoreSeed);
        }
        call.FsmInject("State 2", OnLocalAnswer, index: 0);
        handle.FsmInject("Close phone", OnLocalHangUp, index: 0);

        if (session.IsHost)
        {
            scheduler.FsmInject("Ring", OnHostRing, index: 0);
        }
        else
        {
            scheduler.enabled = false;
            if (jokes != null)
                jokes.enabled = false;
        }
        ready = true;
    }

    void UseSeed()
    {
        savedSeed = Random.seed;
        Random.seed = seed;
    }

    void RestoreSeed() => Random.seed = savedSeed;

    void OnHostRing()
    {
        if (!Active || callObject.activeSelf)
            return;
        seed = Random.Range(int.MinValue, int.MaxValue);
        var w = session.BeginMessage(MessageType.PhoneRing);
        w.WriteString(topic.Value ?? "", MaxTopicChars);
        w.WriteUInt((uint)seed);
        session.Broadcast(Channel.Control);
    }

    void OnLocalAnswer()
    {
        if (answeredByNetwork)
        {
            answeredByNetwork = false;
            return;
        }
        if (!Active)
            return;
        session.BeginMessage(MessageType.PhoneAnswer);
        session.Broadcast(Channel.Control);
    }

    void OnLocalHangUp()
    {
        if (!Active)
            return;
        session.BeginMessage(MessageType.PhoneHangup);
        session.Broadcast(Channel.Control);
    }

    void OnMessage(Connection from, MessageType type, BitReader r)
    {
        if (type != MessageType.PhoneRing && type != MessageType.PhoneAnswer && type != MessageType.PhoneHangup)
            return;
        if (type == MessageType.PhoneRing)
        {
            var newTopic = r.ReadString(MaxTopicChars);
            var newSeed = (int)r.ReadUInt();
            if (r.Overflow || session.IsHost || !ready)
                return;
            ApplyRing(newTopic, newSeed);
            return;
        }
        if (!ready)
            return;
        // Clients only talk to the host, so the host forwards an answer or hang-up to the others.
        if (session.IsHost && session.Connections.Count > 1)
        {
            session.BeginMessage(type);
            session.Broadcast(Channel.Control, from);
        }
        if (type == MessageType.PhoneAnswer)
            ApplyAnswer();
        else
            ApplyHangUp();
    }

    void ApplyRing(string newTopic, int newSeed)
    {
        if (callObject.activeSelf)
            return;
        seed = newSeed;
        topic.Value = newTopic;
        answer.Value = false;
        callObject.SetActive(true);
    }

    // The call FSM leaves ringing for the conversation as soon as Answer is set, just as when the
    // handset is picked up here.
    void ApplyAnswer()
    {
        if (!callObject.activeSelf || answer.Value)
            return;
        if (call.ActiveStateName == ThunderState || System.Array.IndexOf(HandsetOnlyTopics, topic.Value) >= 0)
        {
            ApplyHangUp();
            return;
        }
        answeredByNetwork = true;
        answer.Value = true;
    }

    // What putting the handset down does to the call, without the handset and player parts.
    void ApplyHangUp()
    {
        answer.Value = false;
        answeredByNetwork = false;
        if (!callObject.activeSelf)
            return;
        callObject.SetActive(false);
        if (subtitle != null)
            subtitle.Value = "";
        MasterAudio.StopAllOfSound("Callers");
        MasterAudio.StopAllOfSound("Teimo");
    }
}
