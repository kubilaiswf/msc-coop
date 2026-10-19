using System.Collections;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MSCCoop.Net;
using MSCLoader;
using UnityEngine;

namespace MSCCoop;

// Bought items, shopping bags, beer cases and the like are created by the FSMs under Spawner: each
// counts its objects and names a new one after the count (sausagesx14), and the count is saved, so
// every machine starts from the same numbers. The machine where an item is created runs the game's
// own flow (cart, register, bag) and sends the spawner and number; the others enter the same state
// with the count set one lower, which creates an object of the same name, and put it at the same
// pose. While the level loads every machine recreates the saved items itself, and those are only
// named for the registry.
sealed class SpawnSync(ObjectRegistry registry, FsmSync fsms)
{
    const string SpawnEvent = "MSCCOOP_SPAWN";
    const string LoadState = "Add ID";
    const string BagPrefix = "shoppingbagx";
    const double BagCheckInterval = 0.5;
    const int MaxListItems = 64;

    sealed class Spawner
    {
        public uint Id;
        public PlayMakerFSM Fsm;
        public FsmInt Count;
        public FsmString Prefix;
        public FsmGameObject New;
    }

    readonly Dictionary<uint, Spawner> byId = new();
    readonly Dictionary<PlayMakerFSM, Spawner> byFsm = new();
    readonly Dictionary<GameObject, int> bags = new();
    readonly List<GameObject> bagList = new();
    Session session;
    FsmGameObject bagSpawn;
    Spawner applying;
    GameObject applied;
    Transform spawnPoint;
    bool ready;
    double nextBagCheck;

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

    // Hooked before any Start runs, since the spawners recreate saved items from their Start on.
    public void OnLevelLoaded()
    {
        ready = false;
        byId.Clear();
        byFsm.Clear();
        bags.Clear();
        bagList.Clear();
        if (session == null || Application.loadedLevelName != "GAME")
            return;
        var root = GameObject.Find("Spawner");
        if (root == null)
            return;
        bagSpawn = PlayMakerGlobals.Instance.Variables.FindFsmGameObject("ShoppingBagSpawn");
        spawnPoint = new GameObject("MSCCoop SpawnPoint").transform;
        foreach (var fsm in root.GetComponentsInChildren<PlayMakerFSM>(true))
        {
            var create = SpawnState(fsm);
            var vars = fsm.FsmVariables;
            var count = vars.FindFsmInt("ObjectNumberInt");
            var prefix = vars.FindFsmString("ObjectCreateName");
            var created = vars.FindFsmGameObject("New");
            if (create == null || count == null || prefix == null || created == null)
                continue;
            var spawner = new Spawner { Id = Fnv.Hash32(Path(fsm.transform) + "#" + fsm.FsmName), Fsm = fsm, Count = count, Prefix = prefix, New = created };
            byId[spawner.Id] = spawner;
            byFsm[fsm] = spawner;
            fsm.FsmInject(create, () => OnCreated(spawner));
            if (fsm.GetState(LoadState) != null)
                fsm.FsmInject(LoadState, () => Register(spawner.New.Value));
            fsm.AddSharedGlobalTransition(SpawnEvent, create);
        }
    }

    public void OnGameReady()
    {
        if (session != null)
            ready = byId.Count > 0;
    }

    public void Update()
    {
        if (!Active || bagList.Count == 0 || session.Now < nextBagCheck)
            return;
        nextBagCheck = session.Now + BagCheckInterval;
        for (var i = bagList.Count - 1; i >= 0; i--)
        {
            var bag = bagList[i];
            if (bag == null)
            {
                bags.Remove(bag);
                bagList.RemoveAt(i);
                continue;
            }
            var signature = Signature(bag);
            if (signature == bags[bag])
                continue;
            bags[bag] = signature;
            SendBag(bag, null);
        }
    }

    static string SpawnState(PlayMakerFSM fsm)
    {
        foreach (var t in fsm.FsmGlobalTransitions)
        {
            if (t.EventName == "SPAWNITEM")
                return t.ToState;
        }
        return null;
    }

    void OnCreated(Spawner spawner)
    {
        var go = spawner.New.Value;
        if (applying == spawner)
        {
            applied = go;
            return;
        }
        Register(go);
        if (!Active || go == null)
            return;
        // Items taken out of a bag spawn at the bag and take their freshness from it.
        var source = bagSpawn == null ? null : registry.Find(bagSpawn.Value);
        var w = session.BeginMessage(MessageType.SpawnItem);
        w.WriteUInt(spawner.Id);
        w.WriteUInt((uint)spawner.Count.Value);
        w.WriteUInt(source == null ? 0u : source.Id);
        ObjectMessages.WritePose(w, ToNet(go.transform.position), ToNet(go.transform.rotation));
        session.Broadcast(Channel.Control);
    }

    void Register(GameObject go)
    {
        if (go == null)
            return;
        var added = registry.AddSpawned("i:" + go.name, go);
        if (added != null)
        {
            foreach (var fsm in added)
                fsms.Hook(fsm);
        }
        if (go.name.StartsWith(BagPrefix) && !bags.ContainsKey(go))
        {
            bags[go] = Signature(go);
            bagList.Add(go);
        }
    }

    void OnMessage(Connection from, MessageType type, BitReader r)
    {
        switch (type)
        {
            case MessageType.SpawnItem:
                var id = r.ReadUInt();
                var number = (int)r.ReadUInt();
                var sourceId = r.ReadUInt();
                ObjectMessages.ReadPose(r, out var position, out var rotation);
                if (r.Overflow || !ready)
                    return;
                // Clients only talk to the host, so the host forwards a spawn to the others.
                if (session.IsHost && session.Connections.Count > 1)
                {
                    var w = session.BeginMessage(MessageType.SpawnItem);
                    w.WriteUInt(id);
                    w.WriteUInt((uint)number);
                    w.WriteUInt(sourceId);
                    ObjectMessages.WritePose(w, position, rotation);
                    session.Broadcast(Channel.Control, from);
                }
                if (byId.TryGetValue(id, out var spawner))
                    ApplySpawn(spawner, number, registry.Find(sourceId)?.Go, ToUnity(position), ToUnity(rotation));
                break;
            case MessageType.BagContents:
                ReadBag(from, r);
                break;
        }
    }

    void ApplySpawn(Spawner spawner, int number, GameObject source, Vector3 position, Quaternion rotation)
    {
        if (registry.Has("i:" + spawner.Prefix.Value + number))
            return;
        var count = spawner.Count.Value;
        var previousSpawn = bagSpawn?.Value;
        spawnPoint.position = position;
        spawnPoint.rotation = rotation;
        if (bagSpawn != null)
            bagSpawn.Value = source != null ? source : spawnPoint.gameObject;
        spawner.Count.Value = number - 1;
        applying = spawner;
        applied = null;
        try
        {
            spawner.Fsm.SendEvent(SpawnEvent);
        }
        finally
        {
            applying = null;
            spawner.Count.Value = Mathf.Max(count, number);
            if (bagSpawn != null)
                bagSpawn.Value = previousSpawn;
        }
        if (applied == null)
            return;
        applied.transform.position = position;
        applied.transform.rotation = rotation;
        var body = applied.GetComponent<Rigidbody>();
        if (body != null)
        {
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
        Register(applied);
    }

    // A bag holds counts of what was bought in two lists. They change on the machine where the bag
    // is filled or opened, and the new lists replace the others' copies.
    void SendBag(GameObject bag, Connection except)
    {
        var obj = registry.Find(bag);
        if (obj == null)
            return;
        var w = session.BeginMessage(MessageType.BagContents);
        w.WriteUInt(obj.Id);
        var lists = bag.GetComponents<PlayMakerArrayListProxy>();
        w.WriteByte((byte)lists.Length);
        foreach (var list in lists)
        {
            var items = list._arrayList;
            var count = items == null ? 0 : Mathf.Min(items.Count, MaxListItems);
            w.WriteByte((byte)count);
            for (var i = 0; i < count; i++)
                w.WriteUInt(items[i] is int n ? (uint)n : 0u);
        }
        if (except == null)
            session.Broadcast(Channel.Control);
        else
            session.Broadcast(Channel.Control, except);
    }

    void ReadBag(Connection from, BitReader r)
    {
        var id = r.ReadUInt();
        var listCount = r.ReadByte();
        var values = new List<int[]>();
        for (var l = 0; l < listCount; l++)
        {
            var count = r.ReadByte();
            var items = new int[count];
            for (var i = 0; i < count; i++)
                items[i] = (int)r.ReadUInt();
            values.Add(items);
        }
        if (r.Overflow || !ready)
            return;
        var bag = registry.Find(id)?.Go;
        if (bag == null || !bags.ContainsKey(bag))
            return;
        var lists = bag.GetComponents<PlayMakerArrayListProxy>();
        for (var l = 0; l < lists.Length && l < values.Count; l++)
        {
            var target = lists[l]._arrayList ??= new ArrayList();
            target.Clear();
            foreach (var n in values[l])
                target.Add(n);
        }
        bags[bag] = Signature(bag);
        if (session.IsHost && session.Connections.Count > 1)
            SendBag(bag, from);
    }

    static int Signature(GameObject bag)
    {
        var hash = 17;
        foreach (var list in bag.GetComponents<PlayMakerArrayListProxy>())
        {
            hash = hash * 31 + 7;
            if (list._arrayList == null)
                continue;
            foreach (var item in list._arrayList)
                hash = hash * 31 + (item is int n ? n : 0);
        }
        return hash;
    }

    static string Path(Transform t)
    {
        var path = t.name;
        for (var p = t.parent; p != null; p = p.parent)
            path = p.name + "/" + path;
        return path;
    }

    static Vec3 ToNet(Vector3 v) => new(v.x, v.y, v.z);
    static Quat ToNet(Quaternion q) => new(q.x, q.y, q.z, q.w);
    static Vector3 ToUnity(Vec3 v) => new(v.X, v.Y, v.Z);
    static Quaternion ToUnity(Quat q) => new(q.X, q.Y, q.Z, q.W);
}
