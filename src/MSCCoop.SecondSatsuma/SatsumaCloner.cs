using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace MSCCoop.SecondSatsuma;

// A Satsuma is spread over three roots: the car, its loose parts (CARPARTS) and the part states in
// Database. Unity only remaps references that point inside the hierarchy being instantiated, so the
// three are parented under one holder for the single Instantiate call and put back right after;
// every reference between them then points into the copy.
static class SatsumaCloner
{
    const string CarName = "SATSUMA(557kg, 248)";
    const string CloneCarName = "SATSUMA2(557kg, 248)";
    const string PartsName = "CARPARTS";
    const string CloneSuffix = " 2";
    static readonly string[] DatabaseGroups = { "DatabaseBody", "DatabaseMechanics", "DatabaseMotor", "DatabaseOrders", "DatabaseWiring", "PartsStatus" };

    // Written by one car's FSMs and read by the same car's: the engine model, gauges and the car itself.
    // Globals the player or the radio own (WiringTool, keys, songs) stay shared.
    static readonly string[] CarGlobals =
    {
        "TheCar", "RPM", "SpeedKMH", "CarMass", "CarVelocity", "EngineTemp", "Fuel", "EnginePower",
        "EnginePowerMultiplier", "EnginePowerPerCylinder", "EngineTorqPerCylinder", "EngineFriction",
        "EngineTorque", "EngineTorqueRPM", "JointBattery",
    };

    static readonly Vector3 Offset = new(0f, 0.3f, 0f);
    const float SideOffset = 4.5f;

    static readonly HashSet<Fsm> clones = new();
    static readonly Dictionary<NamedVariable, NamedVariable> globals = new();
    static readonly Dictionary<string, string> renamed = new();
    static readonly Dictionary<Type, FieldInfo[]> fieldCache = new();
    static int rebound;
    static int renamedStrings;

    public static bool IsClone(Fsm fsm) => fsm != null && clones.Contains(fsm);

    public static void Clone()
    {
        clones.Clear();
        globals.Clear();
        renamed.Clear();
        rebound = 0;
        renamedStrings = 0;

        var car = GameObject.Find(CarName);
        var parts = GameObject.Find(PartsName);
        var database = GameObject.Find("Database");
        if (car == null || parts == null || database == null)
        {
            ModConsole.Error("Second Satsuma: the Satsuma, its parts or the database were not found.");
            return;
        }

        var originals = new List<Transform> { car.transform, parts.transform };
        foreach (var group in DatabaseGroups)
        {
            var t = database.transform.Find(group);
            if (t != null)
                originals.Add(t);
        }

        var holder = new GameObject("SecondSatsumaHolder");
        var parents = new Transform[originals.Count];
        var indices = new int[originals.Count];
        for (var i = 0; i < originals.Count; i++)
        {
            parents[i] = originals[i].parent;
            indices[i] = originals[i].GetSiblingIndex();
            originals[i].SetParent(holder.transform, true);
        }

        GameObject copy;
        try
        {
            copy = (GameObject)UnityEngine.Object.Instantiate(holder);
        }
        finally
        {
            for (var i = 0; i < originals.Count; i++)
            {
                originals[i].SetParent(parents[i], true);
                originals[i].SetSiblingIndex(indices[i]);
            }
            UnityEngine.Object.Destroy(holder);
        }

        var cloneCar = copy.transform.GetChild(0);
        var cloneParts = copy.transform.GetChild(1);
        var cloneDatabase = new GameObject("Database2").transform;
        for (var i = copy.transform.childCount - 1; i >= 2; i--)
            copy.transform.GetChild(i).SetParent(cloneDatabase, true);
        cloneCar.SetParent(null, true);
        cloneParts.SetParent(null, true);
        UnityEngine.Object.Destroy(copy);
        cloneCar.name = CloneCarName;
        cloneParts.name = PartsName + "2";

        var shift = car.transform.right * SideOffset + Offset;
        cloneCar.position += shift;
        cloneParts.position += shift;

        CreateGlobals(cloneCar.gameObject);
        RenameParts(cloneParts);
        foreach (var root in new[] { cloneCar, cloneParts, cloneDatabase })
        {
            foreach (var fsm in root.GetComponentsInChildren<PlayMakerFSM>(true))
                clones.Add(fsm.Fsm);
            foreach (var auto in root.GetComponentsInChildren<ES2Auto>(true))
                auto.uniqueTag = SaveKeys.Prefix + auto.uniqueTag;
        }
        // FSMs on active objects were initialised inside Instantiate; the rest are rebound when they wake.
        foreach (var fsm in clones)
        {
            if (fsm.Initialized)
                Rebind(fsm);
        }
        // The co-op mod names objects as they are right after the level loads; it may have done so
        // before this ran, so it is told to look at the scene again.
        var coop = GameObject.Find("MSCCoop");
        if (coop != null)
            coop.SendMessage("OnSceneCloned", SendMessageOptions.DontRequireReceiver);
        ModConsole.Print("Second Satsuma: cloned " + clones.Count + " FSMs, " + renamed.Count + " parts renamed, "
            + rebound + " global references and " + renamedStrings + " part names rebound so far.");
    }

    public static void OnFsmInit(Fsm fsm)
    {
        if (IsClone(fsm))
            Rebind(fsm);
    }

    static void CreateGlobals(GameObject cloneCar)
    {
        var variables = PlayMakerGlobals.Instance.Variables;
        foreach (var name in CarGlobals)
        {
            var original = variables.GetVariable(name);
            if (original == null)
                continue;
            NamedVariable copy = original switch
            {
                FsmFloat f => new FsmFloat(f),
                FsmGameObject g => new FsmGameObject(g),
                FsmVector3 v => new FsmVector3(v),
                FsmObject o => new FsmObject(o),
                FsmBool b => new FsmBool(b),
                FsmInt n => new FsmInt(n),
                _ => null,
            };
            if (copy == null)
                continue;
            if (copy is FsmGameObject car && name == "TheCar")
                car.Value = cloneCar;
            globals[original] = copy;
        }
    }

    // Assembly triggers recognise a part by the name of the object in the hand, so the second car's
    // parts get their own names and its triggers and FSMs are given the same new names below.
    static void RenameParts(Transform cloneParts)
    {
        foreach (var t in cloneParts.GetComponentsInChildren<Transform>(true))
        {
            if (!t.CompareTag("PART") && !t.CompareTag("ITEM"))
                continue;
            var parent = t.parent;
            if (parent != null && (parent.CompareTag("PART") || parent.CompareTag("ITEM")))
                continue;
            renamed[t.name] = t.name + CloneSuffix;
            t.name += CloneSuffix;
        }
    }

    static void Rebind(Fsm fsm)
    {
        foreach (var v in fsm.Variables.StringVariables)
        {
            if (v.Value != null && renamed.TryGetValue(v.Value, out var name))
            {
                v.Value = name;
                renamedStrings++;
            }
        }
        foreach (var state in fsm.States)
        {
            if (!state.ActionsLoaded)
                continue;
            foreach (var action in state.Actions)
            {
                if (action != null)
                    RebindFields(action);
            }
        }
    }

    static void RebindFields(object target)
    {
        foreach (var field in Fields(target.GetType()))
        {
            var value = field.GetValue(target);
            switch (value)
            {
                case NamedVariable variable when globals.TryGetValue(variable, out var replacement):
                    field.SetValue(target, replacement);
                    rebound++;
                    break;
                case FsmString text when !text.UseVariable && text.Value != null && renamed.TryGetValue(text.Value, out var name):
                    text.Value = name;
                    renamedStrings++;
                    break;
                case NamedVariable[] array:
                    for (var i = 0; i < array.Length; i++)
                    {
                        if (array[i] == null)
                            continue;
                        if (globals.TryGetValue(array[i], out var element))
                        {
                            array[i] = element;
                            rebound++;
                        }
                        else if (array[i] is FsmString s && !s.UseVariable && s.Value != null && renamed.TryGetValue(s.Value, out var n))
                        {
                            s.Value = n;
                            renamedStrings++;
                        }
                    }
                    break;
            }
        }
    }

    static FieldInfo[] Fields(Type type)
    {
        if (!fieldCache.TryGetValue(type, out var fields))
        {
            fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            fieldCache[type] = fields;
        }
        return fields;
    }
}
